# Bearing

A context layer. It answers "what does this system call things, what does the schema look like, why is it built this way" — to Claude CLI over MCP, and to anything else that asks.

Named for the machine component, and for what you take to know where you are.

## Shape

```
producers ──writes──▶  corpus/*.md  ──read──▶  Bearing.Core  ──▶  Bearing.Mcp   ──▶ Claude CLI
   (schema gen,          (git repo)              (BM25)          └──▶  CLI verb
    wiki export,                                                 └──▶  GistCast
    decision records)
```

Bearing has one input type: markdown files with front matter. No database connections, no COM, no HTTP clients, no live integrations of any kind.

That constraint is the design, not a limitation:

- **One code path.** Nothing to mock, nothing to stub. Tests drop files in a folder.
- **Can't fail interestingly.** No connection to hang, no auth to expire, no COM apartment to deadlock. The worst case is a stale file.
- **Inspectable.** The entire state of the system can be read with your eyes. Debugging bad retrieval means opening the file it returned.
- **Versioned.** The corpus is a git repo. Refresh is `git pull`, access control is repo permissions, and you can diff what the schema looked like before last month's deploy.
- **Producers are free.** Any language, any host, any schedule. They share nothing with the server but a file format.

The alternative — a server that talks to SQL and Exchange and the wiki directly — is the same functionality with four failure modes, four sets of credentials, and no way to see what it knows.

## Build and run

```bash
dotnet build
dotnet run --project Bearing.Mcp -- --corpus C:\dev\bearing-corpus
```

Corpus root resolves from `--corpus`, then `BEARING_CORPUS`, then `./corpus`.

### Wire into Claude CLI

```json
{
  "mcpServers": {
    "bearing": {
      "command": "C:\\tools\\bearing\\bearing-mcp.exe",
      "args": ["--corpus", "C:\\dev\\bearing-corpus"]
    }
  }
}
```

Verify with the MCP Inspector before wiring it in: `npx @modelcontextprotocol/inspector`.

## Tools

| Tool | Purpose |
|---|---|
| `search_context` | Retrieve passages. Accepts search terms or a whole pasted page. |
| `get_document` | Full text of one document. |
| `list_documents` | What exists, by origin. Orientation at the start of a task. |
| `context_health` | Freshness per origin, and when each producer last ran. |
| `reindex_context` | Force a reload. Rarely needed — the corpus watches its folder. |

Tool descriptions are written carefully on purpose. They're how the model decides whether to call, and a vague description produces a tool that's either never used or used for everything.

## Origins

| Origin | Contains | Stale after |
|---|---|---|
| `schema` | one file per table, generated | 7 days |
| `business` | glossary, conventions, exported wiki pages | 30 days |
| `impl` | architecture notes, repo docs, structural digests | 7 days |
| `decisions` | why something is the way it is | never |

`decisions` is the origin that pays for itself. The reasoning behind a choice — business asked X, we read it as Y, finance objected, here's the resolution — normally lives only in a chat transcript nobody can find in four months. Written down once, it never goes stale, because a decision is a historical fact rather than a description of a moving system.

See `producers/README.md` for the file format and the contract.

## Freshness is a first-class field

Every snippet carries `asOf` and `ageDays`, and `search_context` tells the model to hedge on old material rather than presenting it as current.

`context_health` exists because **a context layer fails silently**. Nobody notices the wiki export broke. Retrieval quietly gets worse; answers become less useful without ever being wrong enough to investigate. That failure mode is the main risk of running one of these at all, so the check that catches it is a tool the model can call and a command you can run — built on day one, not when you eventually get suspicious.

Producers record `_state/{producer}.json` on every run, **including failures**.

## Retrieval

BM25 over heading-aligned chunks, in memory. No embeddings, no vector store, nothing to keep running. A few hundred documents index in well under a second.

Two things worth knowing:

**Why lexical.** This corpus is dominated by proper nouns — table names, column names, screen names, unit numbers, model designations. Lexical matching is better at those than vector similarity, which blurs near-identical identifiers together. Embeddings earn their place when queries stop sharing vocabulary with documents; that's the signal to add one, and `Bm25Index` is behind an interface for when it comes.

**Query construction.** `search_context` takes either search terms or a whole pasted page. A pasted page is mostly common words, so each distinct term is scored by tf × idf against the corpus and the top thirty kept — the corpus decides what's distinctive about the input, which is exactly the judgement you want it making.

## Consumers

**Claude CLI** is the main one, and the reason this is worth building. Every session currently rebuilds domain understanding from scratch. With Bearing wired in, `search_context` grounds it without anyone pasting anything.

**GistCast** references `Bearing.Core` directly and drops its own `Retrieval/` folder:

```csharp
public sealed class BearingContextSource : IContextSource
{
    private readonly Corpus _corpus;

    public string Name => "bearing";

    public BearingContextSource(string corpusRoot) =>
        _corpus = new Corpus(new BearingOptions { CorpusRoot = corpusRoot });

    public Task<IReadOnlyList<ContextSnippet>> GetContextAsync(string content, CancellationToken ct)
        => Task.FromResult(_corpus.Search(content, origin: null, limit: 3, charBudget: 6000));
}
```

In-process, no IPC, no latency. The popup becomes one client of something that outlives it.

**A CLI verb** (`bearing query "..."`, `bearing health`) fits the existing single-file exe pattern and gives AHK and scripts the same access.

## What is deliberately not here

**Live mail.** See `producers/README.md`. Mail splits into durable knowledge — which belongs in `decisions/` as a reviewed record — and live lookup, which shouldn't be materialised at all, on both freshness and governance grounds. Live lookup stays in a separate on-demand tool. Keeping it out is what lets Bearing stay a folder of files.

**Embeddings.** Add when lexical retrieval visibly fails, not before.

**A resident service.** The corpus is single-digit megabytes and indexes in under a second. Every consumer builds its own in memory. Nothing to keep alive, nothing to secure, nothing to be down.

## Verify before trusting

Written against documented API shapes but not built or run — no Windows and no NuGet access here.

- `ModelContextProtocol` was at 1.4.0 as of early July 2026 and the SDK moves quickly. Check the current version and the `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` shape against the SDK docs before assuming the wiring compiles.
- .NET 10 ships a `dotnet new mcpserver` template that may be a cleaner starting point than this hand-rolled host.
- **Never write to stdout.** Stdout carries JSON-RPC frames; anything else there corrupts the protocol and the client fails with an opaque parse error. Logging is pinned to stderr in `Program.cs` — keep it that way, and be careful with any library that logs on its own.
