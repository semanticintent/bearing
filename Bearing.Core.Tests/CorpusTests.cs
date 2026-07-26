using Bearing.Core;

namespace Bearing.Core.Tests;

public sealed class CorpusFixture : IDisposable
{
    public string Root { get; }

    public CorpusFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "bearing-core-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(Root, "schema"));
        Directory.CreateDirectory(Path.Combine(Root, "business"));
        Directory.CreateDirectory(Path.Combine(Root, "decisions"));
        Directory.CreateDirectory(Path.Combine(Root, "_state"));
        Directory.CreateDirectory(Path.Combine(Root, "no_frontmatter"));

        File.WriteAllText(Path.Combine(Root, "schema", "BookCopy.md"), """
            ---
            origin: schema
            label: BookCopy
            asOf: {{RECENT}}
            producer: sqlgen
            ---

            # BookCopy
            One physical item with a barcode.
            """.Replace("{{RECENT}}", DateTimeOffset.UtcNow.AddDays(-2).ToString("O")));

        File.WriteAllText(Path.Combine(Root, "business", "Glossary.md"), """
            ---
            origin: business
            label: Glossary
            asOf: {{STALE}}
            producer: wiki-export
            ---

            # Glossary
            Availability means status AV and not withdrawn.
            """.Replace("{{STALE}}", DateTimeOffset.UtcNow.AddDays(-60).ToString("O")));

        File.WriteAllText(Path.Combine(Root, "decisions", "ExportDefault.md"), """
            ---
            origin: decisions
            label: ExportDefault
            asOf: 2026-03-04T00:00:00Z
            producer: manual
            ---

            # Export defaults to active only
            The checkbox is sticky per user.
            """);

        File.WriteAllText(Path.Combine(Root, "no_frontmatter", "Undocumented.md"),
            "No front matter here, just prose about a widget.");

        File.WriteAllText(Path.Combine(Root, "_state", "sqlgen.json"), """
            {"producer":"sqlgen","lastRun":"2026-07-24T06:12:00Z","success":true,"documentCount":48,"message":"ok"}
            """);
        File.WriteAllText(Path.Combine(Root, "_state", "wiki-export.json"), """
            {"producer":"wiki-export","lastRun":"2026-07-26T02:00:00Z","success":false,"documentCount":0,"message":"export API timed out"}
            """);
    }

    public Corpus NewCorpus() => new(new BearingOptions { CorpusRoot = Root, WatchForChanges = false });

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort cleanup */ }
    }
}

public class CorpusTests : IClassFixture<CorpusFixture>
{
    private readonly CorpusFixture _fixture;

    public CorpusTests(CorpusFixture fixture) => _fixture = fixture;

    [Fact]
    public void Missing_corpus_root_throws_on_load()
    {
        var options = new BearingOptions
        {
            CorpusRoot = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()),
            WatchForChanges = false
        };

        Assert.Throws<DirectoryNotFoundException>(() => new Corpus(options));
    }

    [Fact]
    public void Loads_every_markdown_file_except_state_and_underscore_prefixed()
    {
        using var corpus = _fixture.NewCorpus();

        var all = corpus.List(null);

        Assert.Equal(4, all.Count); // BookCopy, Glossary, ExportDefault, Undocumented
        Assert.DoesNotContain(all, d => d.RelativePath.Contains("_state"));
    }

    [Fact]
    public void A_file_with_no_front_matter_defaults_origin_to_its_top_level_folder()
    {
        using var corpus = _fixture.NewCorpus();

        var doc = corpus.Find("Undocumented");

        Assert.NotNull(doc);
        Assert.Equal("no_frontmatter", doc!.Meta.Origin);
    }

    [Fact]
    public void List_filters_by_origin()
    {
        using var corpus = _fixture.NewCorpus();

        var schemaOnly = corpus.List("schema");

        Assert.Single(schemaOnly);
        Assert.Equal("BookCopy", schemaOnly[0].Label);
    }

    [Fact]
    public void Find_matches_by_label_then_relative_path_then_contains()
    {
        using var corpus = _fixture.NewCorpus();

        Assert.NotNull(corpus.Find("BookCopy"));
        Assert.NotNull(corpus.Find("schema/BookCopy.md"));
        Assert.NotNull(corpus.Find("Book")); // contains-match fallback
    }

    [Fact]
    public void Search_finds_content_across_the_whole_corpus()
    {
        using var corpus = _fixture.NewCorpus();

        var results = corpus.Search("availability", origin: null, limit: 5, charBudget: 4000);

        Assert.Contains(results, r => r.Label.Contains("Glossary"));
    }

    [Fact]
    public void Health_flags_business_as_stale_but_not_schema()
    {
        using var corpus = _fixture.NewCorpus();

        var health = corpus.Health().ToDictionary(h => h.Origin, StringComparer.OrdinalIgnoreCase);

        Assert.True(health["business"].Stale);   // 60 days old, 30-day threshold
        Assert.False(health["schema"].Stale);    // 2 days old, 7-day threshold
    }

    [Fact]
    public void Health_attaches_producer_state_including_recorded_failures()
    {
        using var corpus = _fixture.NewCorpus();

        var health = corpus.Health().ToDictionary(h => h.Origin, StringComparer.OrdinalIgnoreCase);

        Assert.NotNull(health["schema"].LastProducerRun);
        Assert.True(health["schema"].LastProducerRun!.Success);

        Assert.NotNull(health["business"].LastProducerRun);
        Assert.False(health["business"].LastProducerRun!.Success);
    }

    [Fact]
    public void Health_reports_no_producer_run_for_a_manual_decision_record()
    {
        using var corpus = _fixture.NewCorpus();

        var decisions = corpus.Health().Single(h => string.Equals(h.Origin, "decisions", StringComparison.OrdinalIgnoreCase));

        Assert.Null(decisions.LastProducerRun);
    }

    [Fact]
    public void Reload_returns_the_current_document_count()
    {
        using var corpus = _fixture.NewCorpus();

        Assert.Equal(4, corpus.Reload());
    }
}
