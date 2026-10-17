# Changelog

Each release has a section here. The release workflow publishes the section as the
release notes. Vellum uses semantic versions: before 1.0, a minor version can change the
HTTP API, and its section lists each change. A release never changes a database migration
of an earlier release, so the data of each release upgrades to the next.

## Unreleased

- A schema accepted from a scan of code or configuration has the truth label
  `observed-in-source` and a provenance that cites its file and lines, like a channel.
  Version 0.1.0 labelled it `declared` (#5). A stored schema keeps its label; the next
  scan of its Source changes it when nobody edited the schema since.
- HTTP API: a schema has the new fields `factStatus` and `provenance`. The model document
  carries both for each schema; a document without them keeps the label a schema has.
- Relationship sides (#7): each card has a handle on each side. An end of a relationship
  is auto (it faces the other card) or saved per diagram as top, right, bottom or left, by
  a drag of the end or in the Relationship panel. The SVG export and share links use the
  saved sides. API: `PUT /api/projects/{projectId}/views/{viewId}/layout/edges/{relationshipId}`;
  `edges[]` of a view and of a layout save carry `sourceSide` and `targetSide`; a shared
  view has `edges`. Migration `LayoutEdgeSides` adds two nullable columns. The layout
  fingerprint of a view without a saved side does not change.
- Imports apply what they observe. A completed scan of the tracked ref of a deterministic
  Source (git hosts, folder, archive, push, model file, traces, New Relic) applies every
  addition, and each change of a fact that only this Source wrote and nobody edited since,
  with the person who started the scan as the acceptor. A change of a fact a person edited,
  a removal and each question wait in "Needs you". A scan of unchanged source proposes
  nothing for a fact a person edited. Proposals from an AI provider are never applied.
- A rescan deletes nothing. A fact the latest Snapshot no longer holds is marked "not seen
  in latest scan"; a person removes or keeps it. Keep clears the mark until the fact changes.
- The canvas places imported elements with the auto layout and shows the badges "needs you"
  and "not seen". The review dialog, "Proposals by Source" and "Accept all from this Source"
  are removed.
- HTTP API: `GET /api/projects/{projectId}/needs-you` and
  `POST /api/projects/{projectId}/needs-you/keep` are new.
  `POST /api/projects/{projectId}/sources/{sourceId}/accept` and the field
  `review.acceptable` of a Source are removed.

## 0.1.0 - 2026-10-15

The first release.

- Model systems, apps, stores and actors on a canvas with drill-down, with operations,
  channels, messages and JSON schemas as part of the model.
- Trace scenarios of calls, publishes and writes, with payloads and faults.
- Import from Sources: GitHub, GitLab, Bitbucket, Azure DevOps, a local folder, a zip or
  tar.gz upload, a push from the `vellum scan` CLI, Structurizr DSL and workspace JSON,
  Backstage catalog files, ADRs, prose through an AI provider, OpenTelemetry, Jaeger and
  Tempo trace exports, and New Relic. Each import is a Snapshot and a proposal that a
  person reviews. GitLab, Bitbucket, Azure DevOps and New Relic are experimental.
- Truth labels on every fact: `declared`, `observed-in-source`, `inferred`, `measured`,
  `unknown`.
- Promises with stable IDs, obligations and evidence; change impact of a branch or a pull
  request, with a compact report for CI.
- Review rules, decisions (ADRs), drafts, variants and named revisions.
- Read-only share links, embeds, comment threads with mentions, an inbox and webhooks.
- Export: standalone `SDD.md`, an agent bundle, SVG, PNG, Mermaid and Structurizr DSL.
- MCP server for coding agents, with agent tokens that cannot approve.
- Cost estimates and load simulation.
- Local and hosted profiles. Images for linux/amd64 and linux/arm64. The `vellum scan` CLI
  for Linux, macOS and Windows.
