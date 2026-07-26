---
origin: schema
label: BookCopy
asOf: 2026-07-24T06:12:00Z
producer: sqlgen
link: sqlserver://CATALOG-DB/Library/dbo.BookCopy
tags: [catalog, core]
---

# BookCopy

One physical item on the shelf. The business calls these "copies" and never
"items" — "item" is a cataloguing term for something else entirely.

Approximately 60,000 rows. Grows by roughly 300/month.

## Columns

| Column | Type | Null | Notes |
| --- | --- | --- | --- |
| CopyId | int identity | no | Surrogate PK. Never shown to users. |
| Barcode | varchar(20) | no | Business key. Unique. Never reused, even after withdrawal. |
| ISBN | varchar(17) | no | Identifies the edition. Not unique across small-press reprints. |
| TitleId | int | no | FK to Title. |
| BranchCd | varchar(8) | no | FK to Branch. Current holding branch, not originating branch. |
| StatusCd | char(2) | no | See status codes below. |
| AcquiredOn | date | no | |
| WithdrawnOn | date | yes | Non-null means the copy has left the collection. |
| VendorSourced | bit | no | 1 means the row originates in the vendor ingest feed. |

## Status codes

`AV` available · `CO` checked out · `RS` reserved · `LO` lost · `WD` withdrawn

Patrons say "missing" for `LO`, which does not appear anywhere in the data.

## Rules that are not in the constraints

Rows with `VendorSourced = 1` are read-only in this application. They are
overwritten nightly by the vendor feed, so any edit is silently lost. This is
the single most common source of "my change disappeared" tickets.

Queries for the active collection must filter `WithdrawnOn IS NULL`. There is
no view that does this for you.
