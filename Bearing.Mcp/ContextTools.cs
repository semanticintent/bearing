using System.ComponentModel;
using System.Text;
using Bearing.Core;
using ModelContextProtocol.Server;

namespace Bearing.Mcp;

/// <summary>
/// Four tools. The descriptions matter more than the implementations — they are
/// how a model decides whether to call, and a vague description produces a tool
/// that is either never used or used for everything.
/// </summary>
[McpServerToolType]
public sealed class ContextTools
{
    private readonly Corpus _corpus;

    public ContextTools(Corpus corpus) => _corpus = corpus;

    [McpServerTool(Name = "search_context")]
    [Description("""
        Search this organisation's internal context corpus: database schema,
        business domain vocabulary and conventions, implementation notes, and
        decision records explaining why things are the way they are.

        Call this BEFORE answering questions about how a system works, what a
        term means in this business, what a table or column holds, or why a
        behaviour was chosen. The corpus contains internal knowledge that is not
        in your training data and cannot be inferred from code alone.

        Query with the specific terms you care about — table names, screen names,
        domain nouns, model numbers. You may also pass a whole pasted page; the
        distinctive terms are extracted automatically.

        Returns passages with their origin and an ageDays field. Treat large
        ageDays with caution and say so rather than presenting it as current.
        """)]
    public string SearchContext(
        [Description("Search terms, or a block of text to find context for.")]
        string query,
        [Description("Optional filter: schema, business, impl, or decisions.")]
        string? origin = null,
        [Description("Maximum passages to return. Default 5.")]
        int limit = 5)
    {
        var results = _corpus.Search(query, origin, Math.Clamp(limit, 1, 20), charBudget: 12000);

        if (results.Count == 0)
            return "No matching context found. The corpus may not cover this topic — "
                 + "answer from what you have and say the context layer had nothing.";

        var sb = new StringBuilder();

        foreach (var snippet in results)
        {
            sb.AppendLine($"### {snippet.Label}");
            sb.Append($"origin: {snippet.Origin}");

            sb.Append(snippet.AsOf is null
                ? "  ·  asOf: unknown (treat as unverified)"
                : $"  ·  asOf: {snippet.AsOf:yyyy-MM-dd} ({snippet.AgeDays}d old)");

            if (snippet.Link is not null) sb.Append($"  ·  {snippet.Link}");

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(snippet.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_document")]
    [Description("""
        Retrieve one full context document by label or path — for example a
        complete table definition or a whole decision record. Use after
        search_context has returned a passage and you need the rest of it.
        """)]
    public string GetDocument(
        [Description("Document label or relative path, e.g. 'BookCopy' or 'schema/BookCopy.md'.")]
        string label)
    {
        var document = _corpus.Find(label);

        if (document is null)
            return $"No document matching '{label}'. Use list_documents to see what exists.";

        var age = document.Meta.AsOf is null
            ? "unknown"
            : $"{document.Meta.AsOf:yyyy-MM-dd} ({document.AgeDays}d old)";

        return $"""
            # {document.Label}
            origin: {document.Meta.Origin}  ·  asOf: {age}  ·  path: {document.RelativePath}

            {document.Body}
            """;
    }

    [McpServerTool(Name = "list_documents")]
    [Description("""
        List what the context corpus contains, optionally filtered by origin.
        Useful for orienting at the start of a task — seeing which tables,
        guides or decision records exist before searching for one.
        """)]
    public string ListDocuments(
        [Description("Optional filter: schema, business, impl, or decisions.")]
        string? origin = null)
    {
        var documents = _corpus.List(origin);

        if (documents.Count == 0)
            return origin is null ? "Corpus is empty." : $"No documents with origin '{origin}'.";

        var sb = new StringBuilder();

        foreach (var group in documents.GroupBy(d => d.Meta.Origin).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {group.Key} ({group.Count()})");

            foreach (var document in group.OrderBy(d => d.Label))
            {
                var age = document.Meta.AsOf is null ? "?" : $"{document.AgeDays}d";
                sb.AppendLine($"- {document.Label}  ({age})");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "context_health")]
    [Description("""
        Report the freshness of each context origin: document counts, oldest and
        newest asOf dates, whether the origin is stale, and when its producer
        last ran.

        Call this when context seems wrong or incomplete, or before relying on
        the corpus for something consequential. A context layer fails silently —
        a broken export makes answers quietly worse without ever being obviously
        wrong — so this is the check that catches it.
        """)]
    public string ContextHealth()
    {
        var health = _corpus.Health();

        if (health.Count == 0)
            return $"Corpus is empty. Last indexed {_corpus.LastIndexed:u}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Last indexed: {_corpus.LastIndexed:u}");
        sb.AppendLine();

        foreach (var origin in health)
        {
            sb.AppendLine($"## {origin.Origin}{(origin.Stale ? "  — STALE" : "")}");
            sb.AppendLine($"- documents: {origin.Documents}");

            sb.AppendLine(origin.NewestAsOf is null
                ? "- asOf: missing on all documents"
                : $"- asOf range: {origin.OldestAsOf:yyyy-MM-dd} to {origin.NewestAsOf:yyyy-MM-dd} "
                  + $"(oldest {origin.OldestAgeDays}d)");

            if (origin.LastProducerRun is { } run)
            {
                sb.AppendLine($"- producer: {run.Producer}, last run {run.LastRun:u}, "
                            + $"{(run.Success ? "ok" : "FAILED")}, {run.DocumentCount} docs");

                if (!string.IsNullOrWhiteSpace(run.Message))
                    sb.AppendLine($"  {run.Message}");
            }
            else
            {
                sb.AppendLine("- producer: no state recorded");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "reindex_context")]
    [Description("""
        Force a reload of the corpus from disk. Rarely needed — the corpus
        watches its folder and rebuilds automatically after a git pull or a
        producer run. Use if a file was just written and search has not caught up.
        """)]
    public string ReindexContext()
    {
        var count = _corpus.Reload();
        return $"Reindexed {count} documents at {_corpus.LastIndexed:u}.";
    }
}
