using System.Globalization;

namespace Bearing.Core;

/// <summary>
/// Front matter is the entire contract between producers and Bearing.
/// Deliberately a tiny fixed set of scalars — not general YAML — so a producer
/// can emit it with string concatenation in any language, and so a malformed
/// file degrades to "missing metadata" rather than taking the corpus down.
/// </summary>
public sealed class FrontMatter
{
    /// <summary>schema | business | impl | decisions | anything a producer defines.</summary>
    public string Origin { get; init; } = "unknown";

    /// <summary>Human-facing name. Shown to whoever consumes the snippet.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// When the underlying fact was true — NOT when the file was written.
    /// A wiki export run today of a page last edited in March is asOf March.
    /// This is the field people skip and regret.
    /// </summary>
    public DateTimeOffset? AsOf { get; init; }

    /// <summary>Which producer wrote this, for the health report.</summary>
    public string? Producer { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Where a human should go to see the real thing.</summary>
    public string? Link { get; init; }

    /// <summary>
    /// Splits leading `---` fenced front matter from the body.
    /// A file with no front matter is still a valid document; it just carries
    /// no metadata, and the health report will say so.
    /// </summary>
    public static (FrontMatter Meta, string Body) Split(string text, string fallbackLabel)
    {
        var normalised = text.Replace("\r\n", "\n");

        if (!normalised.StartsWith("---\n"))
            return (new FrontMatter { Label = fallbackLabel }, normalised.Trim());

        var end = normalised.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (new FrontMatter { Label = fallbackLabel }, normalised.Trim());

        var block = normalised[4..end];
        var body = normalised[(end + 4)..].TrimStart('\n', '-').Trim();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            fields[trimmed[..colon].Trim()] = trimmed[(colon + 1)..].Trim().Trim('"', '\'');
        }

        return (new FrontMatter
        {
            Origin = Get(fields, "origin") ?? "unknown",
            Label = Get(fields, "label") ?? fallbackLabel,
            AsOf = ParseDate(Get(fields, "asOf")),
            Producer = Get(fields, "producer"),
            Link = Get(fields, "link"),
            Tags = ParseList(Get(fields, "tags"))
        }, body);
    }

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    private static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        return value.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('"', '\''))
            .Where(t => t.Length > 0)
            .ToArray();
    }
}

/// <summary>One markdown file from the corpus.</summary>
public sealed record Document(
    string Path,
    string RelativePath,
    FrontMatter Meta,
    string Body)
{
    public string Label => Meta.Label ?? RelativePath;

    public int AgeDays => Meta.AsOf is null
        ? int.MaxValue
        : (int)(DateTimeOffset.UtcNow - Meta.AsOf.Value).TotalDays;
}

/// <summary>A retrieved passage, with everything a consumer needs to judge it.</summary>
public sealed record ContextSnippet(
    string Label,
    string Origin,
    string Text,
    double Score,
    DateTimeOffset? AsOf,
    string? Link)
{
    public int AgeDays => AsOf is null
        ? int.MaxValue
        : (int)(DateTimeOffset.UtcNow - AsOf.Value).TotalDays;
}
