---
origin: decisions
label: Export defaults to active collection only
asOf: 2026-03-04T00:00:00Z
producer: manual
tags: [catalog, export, reporting]
---

# Export defaults to active collection only

## What was decided

The copy export defaults to `WithdrawnOn IS NULL`. Withdrawn copies are
included only when "Include withdrawn" is explicitly ticked.

## Why

Branch managers were sending the raw export to donors and the withdrawn rows
were being read as available stock. Two separate incidents last year.

Finance objected, because year-end deaccession accounting needs the withdrawn
rows. Resolved by making the checkbox sticky per user rather than per
session, so finance ticks it once and never thinks about it again.

## What this means for changes here

Do not "fix" the export to return everything by default. It looks like a bug
and is not one. If a new export surface is added, it inherits this default.
