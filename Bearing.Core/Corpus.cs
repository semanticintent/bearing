using System.Text.Json;

namespace Bearing.Core;

public sealed class BearingOptions
{
    /// <summary>Root of the markdown corpus. Usually a git clone.</summary>
    public required string CorpusRoot { get; init; }

    /// <summary>Rebuild automatically when files change (git pull, scheduled export).</summary>
    public bool WatchForChanges { get; init; } = true;

    /// <summary>Origins older than this are reported stale. Per-origin overrides below.</summary>
    public int DefaultStaleDays { get; init; } = 14;

    public Dictionary<string, int> StaleDaysByOrigin { get; init; } = new()
    {
        ["schema"] = 7,
        ["impl"] = 7,
        ["business"] = 30,
        ["decisions"] = 3650   // decisions do not go stale; they get superseded
    };
}

/// <summary>What a producer records after each run. Written to _state/{producer}.json.</summary>
public sealed record ProducerState(
    string Producer,
    DateTimeOffset LastRun,
    bool Success,
    int DocumentCount,
    string? Message);

/// <summary>One line of the health report.</summary>
public sealed record OriginHealth(
    string Origin,
    int Documents,
    DateTimeOffset? OldestAsOf,
    DateTimeOffset? NewestAsOf,
    int OldestAgeDays,
    bool Stale,
    ProducerState? LastProducerRun);

/// <summary>
/// The corpus: a folder of markdown, loaded and indexed in memory.
///
/// Bearing has no database, no vector store, no live integrations. Everything
/// it knows arrives as a file that a producer wrote. That single input type is
/// what makes it robust — there is exactly one code path, one failure mode, and
/// the whole state of the system can be read with your eyes.
/// </summary>
public sealed class Corpus : IDisposable
{
    private readonly BearingOptions _options;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    private List<Document> _documents = new();
    private Bm25Index _index = new();
    private Dictionary<string, ProducerState> _producerStates = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset LastIndexed { get; private set; }

    public Corpus(BearingOptions options)
    {
        _options = options;
        Reload();

        if (options.WatchForChanges) StartWatching();
    }

    // ---------- loading ----------

    public int Reload()
    {
        if (!Directory.Exists(_options.CorpusRoot))
            throw new DirectoryNotFoundException($"Corpus root not found: {_options.CorpusRoot}");

        var documents = new List<Document>();
        var index = new Bm25Index();

        var files = Directory
            .EnumerateFiles(_options.CorpusRoot, "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}_state{Path.DirectorySeparatorChar}"))
            .Where(f => !Path.GetFileName(f).StartsWith('_'));

        foreach (var file in files)
        {
            string raw;
            try { raw = File.ReadAllText(file); }
            catch (IOException) { continue; }   // mid-write during a producer run

            // Normalised to forward slashes regardless of OS: this is the form shown
            // in get_document's own examples and in every producer-contract doc, and
            // it must round-trip through Find() the same way on Windows as anywhere
            // else, not just on whichever OS indexed the corpus.
            var relative = Path.GetRelativePath(_options.CorpusRoot, file).Replace('\\', '/');
            var (meta, body) = FrontMatter.Split(raw, relative);

            // Folder name is a sensible default origin, so a producer that
            // forgets front matter still lands somewhere useful.
            if (meta.Origin == "unknown")
            {
                var folder = relative.Contains('/') ? relative[..relative.IndexOf('/')] : null;
                if (!string.IsNullOrWhiteSpace(folder))
                    meta = new FrontMatter
                    {
                        Origin = folder!,
                        Label = meta.Label,
                        AsOf = meta.AsOf,
                        Producer = meta.Producer,
                        Tags = meta.Tags,
                        Link = meta.Link
                    };
            }

            var document = new Document(file, relative, meta, body);
            documents.Add(document);
            index.Add(document);
        }

        index.Build();

        lock (_gate)
        {
            _documents = documents;
            _index = index;
            _producerStates = LoadProducerStates();
            LastIndexed = DateTimeOffset.UtcNow;
        }

        return documents.Count;
    }

    private Dictionary<string, ProducerState> LoadProducerStates()
    {
        var states = new Dictionary<string, ProducerState>(StringComparer.OrdinalIgnoreCase);
        var folder = Path.Combine(_options.CorpusRoot, "_state");

        if (!Directory.Exists(folder)) return states;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<ProducerState>(
                    File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (state is not null) states[state.Producer] = state;
            }
            catch (Exception)
            {
                // A corrupt state file costs one line of the health report.
            }
        }

        return states;
    }

    // ---------- queries ----------

    public IReadOnlyList<ContextSnippet> Search(string query, string? origin, int limit, int charBudget)
    {
        lock (_gate) return _index.Search(query, origin, limit, charBudget);
    }

    public Document? Find(string label)
    {
        lock (_gate)
        {
            return _documents.FirstOrDefault(d =>
                       string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase))
                ?? _documents.FirstOrDefault(d =>
                       string.Equals(d.RelativePath, label, StringComparison.OrdinalIgnoreCase))
                ?? _documents.FirstOrDefault(d =>
                       d.Label.Contains(label, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<Document> List(string? origin)
    {
        lock (_gate)
        {
            return origin is null
                ? _documents.ToList()
                : _documents
                    .Where(d => string.Equals(d.Meta.Origin, origin, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
    }

    /// <summary>
    /// The health report, and the reason it exists: a context layer fails
    /// silently. Nobody notices the wiki export broke; retrieval quietly gets
    /// worse and summaries become less useful without ever being wrong enough
    /// to investigate. This makes staleness a fact you can query rather than a
    /// suspicion you eventually develop.
    /// </summary>
    public IReadOnlyList<OriginHealth> Health()
    {
        lock (_gate)
        {
            return _documents
                .GroupBy(d => d.Meta.Origin, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var dated = group.Where(d => d.Meta.AsOf is not null).ToList();
                    var oldest = dated.Count == 0 ? null : dated.Min(d => d.Meta.AsOf);
                    var newest = dated.Count == 0 ? null : dated.Max(d => d.Meta.AsOf);

                    var threshold = _options.StaleDaysByOrigin.TryGetValue(group.Key, out var days)
                        ? days
                        : _options.DefaultStaleDays;

                    var oldestAge = oldest is null
                        ? int.MaxValue
                        : (int)(DateTimeOffset.UtcNow - oldest.Value).TotalDays;

                    var producer = group
                        .Select(d => d.Meta.Producer)
                        .FirstOrDefault(p => p is not null);

                    return new OriginHealth(
                        group.Key,
                        group.Count(),
                        oldest,
                        newest,
                        oldestAge,
                        oldestAge > threshold,
                        producer is not null && _producerStates.TryGetValue(producer, out var state) ? state : null);
                })
                .OrderBy(h => h.Origin, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ---------- watching ----------

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_options.CorpusRoot, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };

        // A git pull or a producer run touches many files at once. Debounce
        // hard, or you rebuild the index thirty times in two seconds.
        void Schedule(object? _, FileSystemEventArgs __) =>
            _debounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

        _debounce = new Timer(_ =>
        {
            try { Reload(); }
            catch (Exception) { /* next write will try again */ }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _watcher.Changed += Schedule;
        _watcher.Created += Schedule;
        _watcher.Deleted += Schedule;
        _watcher.Renamed += (_, _) => Schedule(null, null!);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
