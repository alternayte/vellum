---
title: Import existing ADRs
description: Turn Markdown architecture decision records in the Nygard or MADR format into proposed decisions, without AI.
---

This guide shows you how to bring the architecture decision records (ADRs) of a repository into the decisions of a project. You need the Editor role to import. An Owner accepts a decision. No AI provider is necessary.

## Where Vellum finds ADRs

Vellum reads Markdown files in these folders, at any depth of the repository:

| Folder | Formats |
|---|---|
| `docs/adr/` | Nygard and MADR |
| `doc/adr/` | Nygard and MADR |
| `docs/decisions/` | MADR |

A file named `README.md` or `index.md`, or with `template` in its name, is not an ADR.

## Import

Do one of these:

- Scan the repository. See [Import a repository](/how-to/import-a-repository/).
- Upload one ADR file. Select **Sources** in the sidebar. Under **Upload a model file**, choose the file. Set **Path in the Source** to a path in one of the folders above, for example `docs/adr/0001-use-postgres.md`. Select **Upload and read**.

When the scan completes, each ADR becomes a decision with the status `proposed` and the origin `imported`. A scan of a pull request, a commit or another ref only previews and proposes no decision.

## Review a decision

1. Select **Check** in the sidebar, then the **Review** tab.
2. Select the **Decisions** tab.
3. Open a decision with the origin `imported`.

The decision shows where it was read: the file and its lines, with the truth label `declared`. It also shows the status and the date that the file records. The status in the file does not change the status in Vellum. A decision stays `proposed` until an Owner accepts or rejects it. See [Record a decision](/how-to/record-a-decision/).

## Scan again after a change

- The same text proposes nothing new.
- Changed text changes a decision that is still proposed and that nobody edited.
- Changed text of a decision that a person edited, accepted or rejected gives a new proposed decision. When the earlier decision is accepted, the new one supersedes it when you accept it.

An ADR that a later scan no longer finds leaves its decision as it is.

## What is kept

Vellum keeps the title, the status, the date, the context, the decision and the consequences. For the exact rules, see [Model file mappings](/reference/model-file-mappings/#adrs).
