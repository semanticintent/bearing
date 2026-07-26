using System.Text;
using System.Text.RegularExpressions;

namespace Bearing.Core;

/// <summary>
/// BM25 over heading-aligned chunks, held in memory.
///
/// Still no embeddings. A corpus of generated schema files, exported wiki pages
/// and decision records is dominated by proper nouns — table names, screen
/// names, unit numbers, model designations — and lexical matching is better at
/// those than vector similarity, which blurs near-identical identifiers
/// together. Reach for embeddings when queries stop sharing vocabulary with
/// documents, not before.
/// </summary>
public sealed partial class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int MaxQueryTerms = 30;
    private const int TargetChunkChars = 1200;

    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private double _averageLength = 1;

    public int ChunkCount => _chunks.Count;

    public void Add(Document document)
    {
        foreach (var (heading, body) in SplitByHeading(document.Body))
        {
            if (string.IsNullOrWhiteSpace(body)) continue;

            var label = string.IsNullOrWhiteSpace(heading)
                ? document.Label
                : $"{document.Label} › {heading}";

            _chunks.Add(new Chunk(
                label,
                document,
                body.Trim(),
                Tokenize(document.Label + " " + heading + " " + body)));
        }
    }

    public void Build()
    {
        _documentFrequency.Clear();

        foreach (var chunk in _chunks)
            foreach (var term in chunk.Terms.Keys)
                _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;

        _averageLength = _chunks.Count == 0 ? 1 : _chunks.Average(c => c.Length);
    }

    public IReadOnlyList<ContextSnippet> Search(
        string query,
        string? origin = null,
        int limit = 5,
        int charBudget = 8000)
    {
        if (_chunks.Count == 0) return Array.Empty<ContextSnippet>();

        var terms = BuildQuery(query);
        if (terms.Count == 0) return Array.Empty<ContextSnippet>();

        var candidates = origin is null
            ? _chunks
            : _chunks.Where(c => string.Equals(c.Document.Meta.Origin, origin, StringComparison.OrdinalIgnoreCase)).ToList();

        var scored = candidates
            .Select(chunk => (chunk, score: Score(chunk, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(limit)
            .ToList();

        var results = new List<ContextSnippet>();
        var used = 0;

        foreach (var (chunk, score) in scored)
        {
            var text = chunk.Text;

            if (used + text.Length > charBudget)
            {
                var remaining = charBudget - used;
                if (remaining < 400) break;
                text = text[..remaining] + "…";
            }

            results.Add(new ContextSnippet(
                chunk.Label,
                chunk.Document.Meta.Origin,
                text,
                Math.Round(score, 3),
                chunk.Document.Meta.AsOf,
                chunk.Document.Meta.Link));

            used += text.Length;
        }

        return results;
    }

    /// <summary>
    /// Works for both short natural-language queries and whole pasted pages.
    /// A pasted page is mostly common words; scoring each distinct term by
    /// tf × idf against this corpus and keeping the top handful lets the corpus
    /// decide what is distinctive about the input, which is exactly the
    /// judgement you want it making.
    /// </summary>
    private Dictionary<string, int> BuildQuery(string text)
    {
        var terms = Tokenize(text);
        if (terms.Count <= MaxQueryTerms) return terms;

        return terms
            .Select(kv => (kv.Key, kv.Value, weight: kv.Value * Idf(kv.Key)))
            .OrderByDescending(x => x.weight)
            .Take(MaxQueryTerms)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    private double Score(Chunk chunk, Dictionary<string, int> query)
    {
        double total = 0;

        foreach (var term in query.Keys)
        {
            if (!chunk.Terms.TryGetValue(term, out var tf)) continue;

            total += Idf(term) * tf * (K1 + 1) /
                     (tf + K1 * (1 - B + B * chunk.Length / _averageLength));
        }

        return total;
    }

    private double Idf(string term)
    {
        var n = _chunks.Count;
        var df = _documentFrequency.GetValueOrDefault(term);
        return Math.Log(1 + (n - df + 0.5) / (df + 0.5));
    }

    // ---------- text handling ----------

    [GeneratedRegex(@"^#{1,6}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"[^\p{L}\p{Nd}_\-]+", RegexOptions.Compiled)]
    private static partial Regex TokenSplitter();

    /// <summary>
    /// Heading-aligned chunks beat fixed windows because they respect the
    /// author's own idea of where one topic stops — and every producer is
    /// asked to emit headings for exactly this reason.
    /// </summary>
    private static IEnumerable<(string Heading, string Body)> SplitByHeading(string text)
    {
        var matches = HeadingPattern().Matches(text);

        if (matches.Count == 0)
        {
            for (var i = 0; i < text.Length; i += TargetChunkChars)
                yield return ("", text.Substring(i, Math.Min(TargetChunkChars, text.Length - i)));
            yield break;
        }

        var pending = new StringBuilder();
        var pendingHeading = "";

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

            if (pending.Length == 0) pendingHeading = matches[i].Groups[1].Value.Trim();
            pending.AppendLine(text[start..end].Trim());

            if (pending.Length >= TargetChunkChars || i == matches.Count - 1)
            {
                yield return (pendingHeading, pending.ToString());
                pending.Clear();
            }
        }
    }

    /// <summary>
    /// No stemming, short stopword list. Stemming collapses identifiers that
    /// must stay distinct, and in a technical corpus that costs more precision
    /// than it buys recall.
    /// </summary>
    private static Dictionary<string, int> Tokenize(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in TokenSplitter().Split(text.ToLowerInvariant()))
        {
            if (raw.Length < 2 || raw.Length > 40) continue;
            if (Stopwords.Contains(raw)) continue;

            counts[raw] = counts.GetValueOrDefault(raw) + 1;
        }

        return counts;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the","and","for","are","but","not","you","all","can","her","was","one","our",
        "out","has","have","had","his","she","him","its","they","them","this","that",
        "with","from","what","when","which","were","will","would","there","their",
        "been","being","into","only","over","then","than","some","such","also","any",
        "each","most","other","more","very","just","how","who","why","use","using",
        "used","get","got","set","new","see","may","must","should","could","about",
        "after","before","between","because","while","where","these","those","upon"
    };

    private sealed record Chunk(string Label, Document Document, string Text, Dictionary<string, int> Terms)
    {
        public int Length { get; } = Terms.Values.Sum();
    }
}
