using Bearing.Core;

namespace Bearing.Core.Tests;

public class Bm25IndexTests
{
    private static Document MakeDocument(string label, string origin, string body, DateTimeOffset? asOf = null) =>
        new($"/{label}", $"{origin}/{label}.md", new FrontMatter { Origin = origin, Label = label, AsOf = asOf }, body);

    [Fact]
    public void Search_before_Build_or_with_no_documents_returns_empty()
    {
        var index = new Bm25Index();

        Assert.Empty(index.Search("anything"));
    }

    [Fact]
    public void Finds_a_document_by_a_distinctive_term()
    {
        var index = new Bm25Index();
        index.Add(MakeDocument("BookCopy", "schema", "# BookCopy\nOne physical item with a barcode and an ISBN."));
        index.Add(MakeDocument("Glossary", "business", "# Glossary\nAvailability means status AV and not withdrawn."));
        index.Build();

        var results = index.Search("what does availability mean");

        Assert.NotEmpty(results);
        Assert.Equal("Glossary › Glossary", results[0].Label);
    }

    [Fact]
    public void Small_adjacent_headings_coalesce_into_one_chunk()
    {
        // Chunking targets ~1200 chars, so headings smaller than that merge
        // rather than each becoming its own tiny chunk.
        var index = new Bm25Index();
        index.Add(MakeDocument("Doc", "impl", "# Doc\n## First section\nAlpha content here.\n## Second section\nBeta content here."));
        index.Build();

        Assert.Equal(1, index.ChunkCount);
    }

    [Fact]
    public void A_heading_with_enough_content_splits_off_into_its_own_chunk()
    {
        var bigSection = string.Concat(Enumerable.Repeat("alpha content filler text here. ", 50)); // ~1650 chars
        var index = new Bm25Index();
        index.Add(MakeDocument("Doc", "impl", $"## First\n{bigSection}\n## Second\nBeta content here."));
        index.Build();

        Assert.Equal(2, index.ChunkCount);
    }

    [Fact]
    public void A_document_with_no_headings_still_indexes_as_one_or_more_chunks()
    {
        var index = new Bm25Index();
        index.Add(MakeDocument("Doc", "impl", "Just a wall of text with no markdown headings at all."));
        index.Build();

        Assert.True(index.ChunkCount >= 1);
    }

    [Fact]
    public void Origin_filter_excludes_other_origins()
    {
        var index = new Bm25Index();
        index.Add(MakeDocument("BookCopy", "schema", "# BookCopy\nBarcode identifies the copy."));
        index.Add(MakeDocument("Glossary", "business", "# Glossary\nBarcode identifies the copy in plain language too."));
        index.Build();

        var results = index.Search("barcode", origin: "schema");

        Assert.All(results, r => Assert.Equal("schema", r.Origin));
    }

    [Fact]
    public void A_query_of_only_stopwords_matches_nothing()
    {
        var index = new Bm25Index();
        index.Add(MakeDocument("Doc", "impl", "# Doc\nSome real content about a barcode."));
        index.Build();

        Assert.Empty(index.Search("the and but for"));
    }

    [Fact]
    public void Limit_caps_the_number_of_results()
    {
        var index = new Bm25Index();
        for (var i = 0; i < 10; i++)
            index.Add(MakeDocument($"Doc{i}", "impl", $"# Doc{i}\nBarcode content number {i}."));
        index.Build();

        var results = index.Search("barcode", limit: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void CharBudget_truncates_and_marks_truncation()
    {
        var index = new Bm25Index();
        index.Add(MakeDocument("Doc", "impl", "# Doc\n" + string.Concat(Enumerable.Repeat("barcode content ", 200))));
        index.Build();

        var results = index.Search("barcode", charBudget: 500);

        Assert.Single(results);
        Assert.True(results[0].Text.Length <= 501);
        Assert.EndsWith("…", results[0].Text);
    }

    [Fact]
    public void Result_carries_the_documents_asOf_and_link_through()
    {
        var asOf = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        var doc = new Document("/BookCopy", "schema/BookCopy.md",
            new FrontMatter { Origin = "schema", Label = "BookCopy", AsOf = asOf, Link = "sqlserver://x" },
            "# BookCopy\nBarcode identifies the copy.");

        var index = new Bm25Index();
        index.Add(doc);
        index.Build();

        var result = index.Search("barcode").Single();

        Assert.Equal(asOf, result.AsOf);
        Assert.Equal("sqlserver://x", result.Link);
    }
}
