# The producer contract

Bearing reads markdown. It has no database connections, no COM, no HTTP clients, and no knowledge of where anything came from. Everything it knows arrived as a file that a producer wrote.

A producer is any process that writes markdown into the corpus. Write them in whatever you like, run them wherever makes sense, on whatever schedule the source deserves.

## The file format

```markdown
---
origin: schema
label: BookCopy
asOf: 2026-07-24T06:12:00Z
producer: sqlgen
link: sqlserver://CATALOG-DB/Library/dbo.BookCopy
tags: [catalog, core]
---

# BookCopy

## Columns
...
```

| Field | Required | Notes |
|---|---|---|
| `origin` | no | Defaults to the top-level folder name. |
| `label` | no | Defaults to the relative path. |
| `asOf` | **yes, in practice** | When the *fact* was true, not when the file was written. |
| `producer` | yes | Links the document to its `_state` entry. |
| `link` | no | Where a human goes to see the real thing. |
| `tags` | no | |

**`asOf` is the field to get right.** A wiki export that runs tonight over a page last edited in March is `asOf` March. Stamping it with the export time makes stale content look fresh, which is worse than having no date at all — a consumer that knows a document is old can hedge; one that has been told it's current cannot.

## Rules for producers

**Emit headings.** Chunking follows `#` headings. A wall of text with no headings becomes one large chunk and retrieval can't discriminate inside it.

**One subject per file.** One table, one guide, one decision. A single dumped schema retrieves badly no matter how good it is — the whole point of splitting is that BM25 can then tell tables apart.

**Include the vocabulary humans use.** This is the difference between a corpus that works and one that doesn't. `StatusCd` never matches a query about "availability" or "copies that are missing". Every generated document needs a sentence of human description at the top, and generators can't invent that — pull it from `MS_Description` extended properties, or keep a small hand-maintained sidecar the generator merges in. That sidecar is the only handwritten part, it's a couple of hundred lines, and it changes twice a year.

**Write the whole file atomically.** Write to `.tmp`, then move. The corpus watches the folder and a half-written file gets read.

**Record your run.** Drop `_state/{producer}.json`:

```json
{
  "producer": "sqlgen",
  "lastRun": "2026-07-24T06:12:00Z",
  "success": true,
  "documentCount": 48,
  "message": "Catalog schema, post-deploy hook, release 2026.7.3"
}
```

Write this even when the run fails, with `success: false` and the error in `message`. A producer that fails silently is exactly the failure `context_health` exists to catch, and it can only catch it if failure gets recorded.

## Cadence follows whoever owns the truth

| Origin | Producer | Runs |
|---|---|---|
| `schema` | reflect over `sys.tables`, one file per table | post-deploy hook, per environment |
| `business` | wiki/Confluence export API | nightly |
| `impl` | repo docs, plus a structural digest if needed | on merge to main |
| `decisions` | hand-written, or drafted from a thread and reviewed | when a decision is made |

Never hand-maintain a copy of something a system already knows.

## Mail

There is no mail producer, and that's deliberate.

Mail splits into two things that look alike and behave completely differently:

**Durable knowledge** — "business decided the export defaults to the active collection, here's why, here's who objected." That's a decision record. It belongs in `decisions/`, written once, reviewed by a human, and it never goes stale because a decision is a historical fact. Drafting it from a thread is a fine use of an assistant; committing it unreviewed is not.

**Live lookup** — "what's the latest on the export thread?" That's not knowledge, it's a query against a system of record. It should never be materialised, for two reasons. It's wrong the moment a reply lands. And writing customer correspondence to markdown on disk puts it outside Exchange retention and eDiscovery, in a folder that's probably synced somewhere, in a format nobody is governing. A screenshot is one lapse; a scheduled export is a policy.

So live mail lookup stays out of Bearing entirely — it belongs in a separate tool that queries Outlook on demand and persists nothing. You already have one. Keeping it out is what lets Bearing stay a folder of files.

## Impl: index the docs, not the source

Indexing the codebase sounds right and mostly isn't. It's enormous, it churns constantly, and when someone asks how a screen connects to the wider application the answer lives in your README and architecture notes, not in four hundred files of C#.

If retrieval later turns out to be missing things, generate a **structural digest** — project layout, public type surface, endpoint list, job schedule — rather than indexing implementation. It's a fraction of the size and it's the part that's actually stable enough to be worth remembering.
