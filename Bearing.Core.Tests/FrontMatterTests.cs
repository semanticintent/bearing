using Bearing.Core;

namespace Bearing.Core.Tests;

public class FrontMatterTests
{
    [Fact]
    public void A_file_with_no_front_matter_is_still_valid()
    {
        var (meta, body) = FrontMatter.Split("Just a plain markdown file.\n", "fallback/path.md");

        Assert.Equal("unknown", meta.Origin);
        Assert.Equal("fallback/path.md", meta.Label);
        Assert.Equal("Just a plain markdown file.", body);
    }

    [Fact]
    public void Parses_all_scalar_fields()
    {
        var raw = """
            ---
            origin: schema
            label: BookCopy
            asOf: 2026-07-24T06:12:00Z
            producer: sqlgen
            link: sqlserver://CATALOG-DB/Library/dbo.BookCopy
            tags: [catalog, core]
            ---

            # BookCopy
            body text
            """;

        var (meta, body) = FrontMatter.Split(raw, "schema/BookCopy.md");

        Assert.Equal("schema", meta.Origin);
        Assert.Equal("BookCopy", meta.Label);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 6, 12, 0, TimeSpan.Zero), meta.AsOf);
        Assert.Equal("sqlgen", meta.Producer);
        Assert.Equal("sqlserver://CATALOG-DB/Library/dbo.BookCopy", meta.Link);
        Assert.Equal(new[] { "catalog", "core" }, meta.Tags);
        Assert.StartsWith("# BookCopy", body);
    }

    [Fact]
    public void AsOf_is_the_fact_date_not_the_write_date_and_missing_fields_default_sensibly()
    {
        var raw = """
            ---
            origin: business
            asOf: not-a-real-date
            ---
            body
            """;

        var (meta, _) = FrontMatter.Split(raw, "business/glossary.md");

        Assert.Null(meta.AsOf);
        Assert.Null(meta.Producer);
        Assert.Empty(meta.Tags);
        Assert.Equal("business/glossary.md", meta.Label);
    }

    [Fact]
    public void Unterminated_front_matter_degrades_to_no_metadata_rather_than_throwing()
    {
        var raw = "---\norigin: schema\nno closing fence here";

        var (meta, body) = FrontMatter.Split(raw, "fallback.md");

        Assert.Equal("unknown", meta.Origin);
        Assert.Contains("no closing fence", body);
    }

    [Fact]
    public void Document_AgeDays_is_MaxValue_when_AsOf_is_absent()
    {
        var doc = new Document("path", "rel", new FrontMatter(), "body");

        Assert.Equal(int.MaxValue, doc.AgeDays);
    }

    [Fact]
    public void Document_AgeDays_reflects_asOf_not_now()
    {
        var meta = new FrontMatter { AsOf = DateTimeOffset.UtcNow.AddDays(-10) };
        var doc = new Document("path", "rel", meta, "body");

        Assert.InRange(doc.AgeDays, 9, 11);
    }

    [Fact]
    public void Document_Label_falls_back_to_relative_path_when_unset()
    {
        var doc = new Document("path", "schema/BookCopy.md", new FrontMatter(), "body");

        Assert.Equal("schema/BookCopy.md", doc.Label);
    }
}
