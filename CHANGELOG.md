# Changelog

Each release has a section here. The release workflow publishes the section as the
release notes. Vellum uses semantic versions: before 1.0, a minor version can change the
HTTP API, and its section lists each change. A release never changes a database migration
of an earlier release, so the data of each release upgrades to the next.

## 0.2.0 - 2026-10-17

A new project reaches a connected diagram of its code in three steps: set up, choose a
folder, see the diagram.

- First run: setup asks for email, password and the setup code. Each new account gets the
  workspace "My workspace" and the empty project "My system", and opens it.
- An empty project offers three starts: **Choose folder / GitHub URL**, **Describe it /
  Type it** and **Open the sample**. **Choose folder** reads a copy of a local folder in the
  browser, applies `.gitignore`, uploads only the files a scan reads as an archive Source,
  and never writes to the folder. **Type it** writes `declared` elements and relationships
  from lines such as `Web app -> Orders API: places orders`, with no AI key.
- The project menu has **New project…**, **New workspace…** and **All projects**.
- The sidebar has five areas: Model, Flows, Check, Ship and Sources. The former tools are
  tabs in them. Check and Ship show when the model holds an element. The assistant opens
  from a button on the canvas and from ⌘K; comments are a tab of the bell. The sidebar
  collapses to icons, with tooltips.
- AI proposals from the assistant and from `propose_architecture` show as a ghost layer of
  dashed cards and lines on the canvas, with **Accept all**, **Reject all** and accept or
  reject on each item. Accepting an item accepts what it depends on. The proposal dialog is
  removed. A proposal an agent submits notifies the person who made its token.
- A refused accept lists every refused item with its reason (#1). Proposals have select
  all and accept all (#3).
- `propose_architecture` drops an item that an accept would refuse, such as a store with
  no system parent (#2), and lists a relationship the model holds already in
  `resolvedToExisting` (#4).
- A scenario exports as a Mermaid `sequenceDiagram` and as SVG (#6). API:
  `GET /api/projects/{projectId}/scenarios/{scenarioId}/export/mermaid` and `/export/svg`.
- Derived lines: a relationship whose ends are hidden inside collapsed cards draws as a line
  between their nearest visible cards, merged per direction ("3 relationships"). Selecting
  it lists the relationships. Exports and share links draw the same lines; a view share
  link shows the label and count only and names no hidden part. Structurizr DSL exports
  them with the tag `Derived`, which an import skips.
- Tidy routes lines around cards and keeps a label on each line. Migration `LayoutLines`
  adds `views.layout_lines` for the routes of derived lines; `PUT …/layout` takes `lines`.
  A card that a later import adds lands in free space.
- A Compose service that runs only an image Vellum does not recognise becomes a top-level
  system tagged `external`. Tools such as proxies, telemetry and mail catchers do not.
- Enter saves a single-line field of the detail panel and Escape reverts it.
- The OpenAPI document sorts its paths and tags, so a build always gives the same file.
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
- A code scan proposes relationships: a producer to each consumer of a message ("publishes
  <message>", through bindings as the trace engine routes), an app to the store it opens
  ("reads and writes", matched through the Compose variable or `depends_on`), and each
  Compose `depends_on` between two elements ("depends on"). They are `observed-in-source`,
  apply like other facts and get "not seen in latest scan" when their evidence goes.
- A variable such as `PAYMENTS_URL`, or an HTTP client base address key, that names another
  app in the Compose file gives an `inferred` "calls" relationship with a confidence. A scan
  never applies it; it waits in "Needs you" as a suggestion. HTTP API: a proposal has the
  field `confidence`; `factStatus` can be `inferred`.
- New extraction rules `dotnet.db-client`, `dotnet.http-client-base-address` and
  `relationship.*`; `go.sql-open` (version 3) records the variable of `os.Getenv`. No scan
  proposes a relationship the model holds with the same ends, kind and label.
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
