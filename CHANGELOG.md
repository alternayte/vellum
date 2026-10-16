# Changelog

Each release has a section here. The release workflow publishes the section as the
release notes. Vellum uses semantic versions: before 1.0, a minor version can change the
HTTP API, and its section lists each change. A release never changes a database migration
of an earlier release, so the data of each release upgrades to the next.

## Unreleased

- Relationship sides (#7): each card has a handle on each side. An end of a relationship
  is auto (it faces the other card) or saved per diagram as top, right, bottom or left, by
  a drag of the end or in the Relationship panel. The SVG export and share links use the
  saved sides. API: `PUT /api/projects/{projectId}/views/{viewId}/layout/edges/{relationshipId}`;
  `edges[]` of a view and of a layout save carry `sourceSide` and `targetSide`; a shared
  view has `edges`. Migration `LayoutEdgeSides` adds two nullable columns. The layout
  fingerprint of a view without a saved side does not change.

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
