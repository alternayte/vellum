# Vellum v1 — Consolidated Design Spec

> Consolidated from PRD.md, SDD.md, UI_UX.md, and open_points.md. This is the build-from document.

---

## 1. What we're building

A self-hostable, open-source web app for designing, planning, and documenting software architecture. One model is the single source of truth — every diagram, doc, and view is a projection of it.

Three capabilities in one tool:
1. **C4-first visual modelling** — interactive, zoomable diagrams with visual quality matching or beating IcePanel.
2. **Design-and-plan workflow** — Git-style draft → review → merge over the architecture model, with decision records.
3. **Integrated documentation** — technical and product docs with live diagrams embedded from the same model.

An optional event/message overlay sits on top as a toggleable lens.

---

## 2. Tech stack

- **Backend:** .NET 10 / C# 14 / EF Core 10 / ASP.NET Core minimal APIs
- **Frontend:** React 19 SPA (Vite 6.x) / TanStack Query v5 / React Flow v12 / shadcn/ui + Tailwind v4
- **Database:** PostgreSQL 17 (single datastore, no Redis/broker/separate IdP)
- **Exhaustive unions:** Thinktecture.Runtime.Extensions v9.x (rip out when C# 15 / .NET 11 ships native unions, ~Nov 2026)
- **Auth:** ASP.NET Core Identity (cookie sessions) + external OIDC (GitHub/Google)
- **API contract:** OpenAPI document generation → `@hey-api/openapi-ts` for typed frontend client
- **MCP:** in-process via C# MCP SDK (`ModelContextProtocol` NuGet)
- **Dev tooling:** `dotnet` CLI, `dotnet watch`, `justfile`, `docker-compose` for Postgres, Testcontainers
- **Docs authoring:** MDX v3 rendered client-side, CodeMirror editor with MDX syntax highlighting
- **Layout:** elkjs for auto-layout assist; manual placement is primary

All dependencies MIT or Apache-2.0.

---

## 3. Architecture

Single-deployable modular monolith: one .NET process serves HTTP API, MCP endpoint, background workers, and the built static SPA. One PostgreSQL instance is the only dependency.

```
┌──────────────────────────────────────────────┐
│  .NET 10 process                             │
│  ┌──────────┐ ┌──────────┐ ┌──────────────┐ │
│  │ HTTP API │ │ MCP (in- │ │ Background   │ │
│  │ (minimal │ │ process) │ │ workers      │ │
│  │  APIs)   │ │          │ │              │ │
│  └────┬─────┘ └────┬─────┘ └──────┬───────┘ │
│       └────────────┴── domain ────┘          │
│                     │                        │
│              serves static SPA (React)       │
└─────────────────────┬────────────────────────┘
                      │
                ┌─────┴─────┐
                │ PostgreSQL │
                └───────────┘
```

### Solution structure (package-by-feature)

No mediator, no generic repository, no AutoMapper. Vertical slices: each feature is one file holding its command/query + validator + handler + endpoint. Transaction decorator via Scrutor wraps command handlers.

```
src/Vellum/
  Program.cs
  Kernel/                    # event store, projections, merge engine, Result, validation, outbox
  Modules/
    Modelling/               # C4 model — event-sourced (elements, relationships, messages)
    Drafts/                  # branch/review/merge over the model stream
    Views/                   # saved projections + layout (NOT event-sourced)
    Flows/                   # ordered narratives over a view
    Docs/                    # MDX docs, ADRs, spaces (NOT in model stream)
    Schemas/                 # message contracts (overlay)
    Identity/                # ASP.NET Core Identity + external OIDC
    Workspaces/              # tenancy: workspaces, projects, membership, share links
  Workers/                   # async projection host, outbox dispatcher
  ClientApp/                 # React SPA
tests/Vellum.Tests/          # aggregate, integration, convention, endpoint tests
```

DbContext-per-module, each mapped to its own Postgres schema (`es`, `modelling`, `views`, `docs`, `schemas`, `identity`, `workspaces`).

---

## 4. Domain model

### Elements

A node in the model. `kind ∈ {actor, system, app, store, component}`. Fields: name, description, technology, owner, tags, status (`current | planned | deprecated | removed`), parent (containment). Containment produces C4 zoom: system → app → component.

### Relationships

Directed connection between elements. Fields: from, to, label, technology, optional message ref.

### Messages (overlay)

First-class event/command node referencing a Schema. Producer → message → consumer(s). Hidden by default; shown when event lens is on.

### Schemas

Versioned contract (JSON Schema / AsyncAPI / OpenAPI fragment) attached to a message or service.

### Views

Saved projection of the model: root element, visible elements, active filters/lens, active flow. Many views, one model.

### Layout

Per-view positioning sidecar (`elementId → {x, y}`, edge routing). Kept separate from semantic model.

### Flows

Ordered sequence of steps referencing elements or relationships, overlaid on a view.

### Drafts

Named branch of the model, stored as a diff overlay on top of main. Renders as base + overlay. Reviewable as changeset; mergeable into main.

### Decision records (ADR)

Doc capturing why a change was made, anchored to draft and affected elements.

### Docs

Long-form MDX. Attached to an element or standalone in spaces. Supports embedded live views and cross-linked elements.

### Tags / Owners / Teams

Metadata used as visual lenses and filters.

### Workspace → Project → User

Tenancy and grouping. Share links provide public read-only access.

---

## 5. Event-sourcing kernel

### Storage

```sql
-- schema: es
create table es.streams (
  stream_id    uuid primary key,
  stream_type  text not null,
  version      int  not null,
  state        jsonb not null,
  created_at   timestamptz not null default now(),
  updated_at   timestamptz not null default now()
);

create table es.events (
  stream_id       uuid not null references es.streams(stream_id),
  version         int  not null,
  global_position bigint generated always as identity,
  event_type      text not null,
  payload         jsonb not null,
  metadata        jsonb not null,
  occurred_at     timestamptz not null default now(),
  xid             xid8 not null default pg_current_xact_id(),
  primary key (stream_id, version)
);
```

### Write path

`Decide(state, command) → events` (pure). `Evolve(state, events) → newState` (pure fold). Events + new snapshot written atomically with optimistic concurrency `where version = :expected`. Loading reads the snapshot, never replays.

Commands and events are closed unions (Thinktecture), so Decide/Evolve/upcasters are exhaustive.

### Projections

- **Inline** (same transaction): immediately consistent read models for the canvas.
- **Async** (background host): eventually consistent. Tracks position via checkpoints; uses `select … for update skip locked` for single-runner election; `pg_snapshot_xmin` xid guard for gap safety; `listen/notify` for wake.

### Upcasting

Versioned event type names (`…added.v2`). Declarative registry maps CLR type to current version + upcaster chain. Old events never migrated.

---

## 6. Branching & merge

### Aggregate boundary

One branchable stream per (project, branch). The whole C4 model for a project is the aggregate. Element/relationship IDs are random UUIDs that persist across branches.

### Drafts

New stream seeded from main's snapshot at fork point. Draft edits append to draft stream only.

### Three-way merge

Three states by entity ID: base = main@fork_point, ours = main@current, theirs = draft@head.

| base→ours | base→theirs | result |
|-----------|-------------|--------|
| unchanged | changed | take theirs (auto) |
| changed | unchanged | keep ours (auto) |
| changed (same) | changed (same) | take either (auto) |
| changed (differently) | changed (differently) | **conflict** |
| deleted | modified | **conflict** |
| added (distinct IDs) | added (distinct IDs) | keep both |

Conflicts surfaced for manual resolution. On success, resolved changes re-emitted as events on main in one transaction (merge commit), tagged with draft_id + merge correlation ID.

---

## 7. API design

OpenAPI-first. Minimal-API endpoints per vertical slice. Commands return `Result` (typed union), mapped to HTTP status. Optimistic concurrency failures → `409`.

- No version prefix in v1 (`/api/projects/{id}/elements`)
- Cookie-based auth on all endpoints except share links and health probes
- Role-based auth: Viewer (read) / Editor (read+write+drafts) / Owner (full)
- Rate limiting: 200 reads/min, 60 writes/min, 100 share-link/min per user/token
- Cursor-based pagination (default 50, max 200)
- Client-supplied UUIDs for idempotent creates
- Consistent error envelope with type, title, detail, field-level errors

---

## 8. Frontend architecture

### Data layer

React Query over the Hey API generated client. Read models drive the canvas; mutations post commands and invalidate queries.

### Canvas (React Flow)

Custom C4-styled nodes. Built on top of React Flow:
- **Semantic zoom / LoD** — two-tier: full card (when text legible) ↔ label chip (when zoomed out). Triggered by rendered text legibility.
- **Drill-in navigation** — double-click descend into element's internals; breadcrumb for ascent. Zoom-through transition (respects `prefers-reduced-motion`).
- **Auto-layout** via elkjs as assist ("Tidy" action); manual placement is primary, persisted per view.
- **Lenses** — metadata, status, flows, message overlay, presentation — all client-side filters over one loaded model.

### Node design

Full card: kind-spine (coloured left edge by kind), name, description, tech as mono badges, owner avatar, status on border (dashed for planned, amber tint for deprecated). Stores get cylinder silhouette; actors get rounded/person affordance.

Label chip: name + kind icon + status colour only.

### Edge design

Orthogonal routing with rounded corners. Labels on mid-edge chip. Message lens expands edges to producer → message pill → consumer(s).

### App shell

Three columns + bars (shadcn/Tailwind):
- **Top bar:** project selector, branch/draft pill, search, share, ⌘K
- **Navigator (left):** model tree + saved views
- **Canvas (center):** React Flow with breadcrumb above
- **Detail panel (right):** shadcn Sheet for selected element/relationship
- **Bottom bar:** lens toggles, flow stepper, zoom, diff legend

### Docs

MDX in CodeMirror editor with syntax highlighting. No preview pane in v1. `<LiveView/>` component embeds live views. Whitelisted/sandboxed components.

---

## 9. Design language

Dense, technical-first by default. Cartographic/technical drafting identity.

### Palette (dark default)

- `Ink` #14181D — dark canvas/background
- `Graphite` #1E242B — dark surfaces/panels
- `Paper` #F6F7F7 — light-mode background
- `Plotter` #0E8EA8 — brand accent (drafting cyan, used sparingly)
- 10-step graphite ramp for neutrals

### Functional colours

- Status: current (neutral) · planned #3B7DD8 · deprecated #B7791F · removed #B5483D
- Diff: added #2F855A · modified #B7791F · removed #B5483D

### Typography

- Display: technical grotesque (Space Grotesk or similar)
- Body/UI: legible neutral sans (Inter/Geist)
- Data/mono: precise monospace (IBM Plex Mono / JetBrains Mono)

### Signature

Strata wayfinding breadcrumb + C4 node card (kind-spine + mono tech badge + status edge).

---

## 10. Auth & multi-tenancy

- ASP.NET Core Identity with httpOnly cookie sessions + external OIDC (GitHub/Google)
- Row-level `workspace_id` on tenant-scoped tables
- Workspace → Project → membership (owner/editor/viewer)
- Share links: opaque 256-bit tokens, public read-only access to views/docs

---

## 11. Background jobs & outbox

Hand-rolled on Postgres (no Hangfire). Outbox table + job tables drained by worker using `select … for update skip locked`.

- Outbox: 5 retries with exponential backoff → dead letters table
- Async projections: 3 retries per event, then skip + health check degraded
- Merge: single synchronous transaction, no server-side retry

---

## 12. Testing strategy

Testcontainers-backed Postgres.

| Layer | What it covers |
|-------|---------------|
| Aggregate (unit) | Decide/Evolve logic, all event types, edge cases |
| Merge (unit) | Every conflict matrix row, multi-entity merges, resolution |
| Integration | Command → event store → inline projection → query |
| Convention | Package-by-feature structure, naming, no raw SQL, auth checks |
| Endpoint | HTTP request/response, status codes, error format, auth |
| E2E (future) | Happy-path Playwright once SPA stabilises |

Merge engine and gap-safe async projections get heaviest coverage.

---

## 13. Build order (Approach A — SDD phases)

Dependency-ordered:

1. **Phase 0 — Foundation:** ES kernel (streams, events, snapshots, decide/evolve, inline projections, async projection host, outbox, upcasting registry). Validation gate: aggregate tests pass, snapshot round-trips, projection host processes without gaps.
2. **Phase 1 — Modelling:** C4 elements/relationships CRUD, canvas with React Flow custom nodes, semantic zoom, drill-in navigation, metadata/status lenses, views + layout persistence, auto-layout assist. Validation gate: elements/relationships on canvas, semantic zoom across 3 C4 levels, layout persists.
3. **Phase 2 — Docs:** MDX editor, `<LiveView/>` embeds, cross-linking, element-attached + standalone docs in spaces. Validation gate: MDX renders, live view embeds update with model, cross-links work.
4. **Phase 3 — Planning:** Drafts (fork/edit), review (structural diff/changeset), merge (three-way with conflict resolution), ADRs linked to drafts, comments on elements/drafts. Validation gate: full fork → edit → review → merge flow works end-to-end including conflicts.
5. **Phase 4 — Bridge & AI:** Message overlay lens, schema attach, MCP query ("what depends on X?"), YAML/JSON export. Validation gate: message lens renders, MCP answers queries, export round-trips.
6. **Auth & multi-tenancy (parallel from Phase 1+):** Login via OIDC, workspace isolation, share links, role enforcement.
7. **Deployment:** Docker container with embedded SPA + docker-compose with Postgres.

---

## 14. Decisions log

| Decision | Resolution |
|----------|-----------|
| Tech stack | .NET 10 + React SPA + PostgreSQL |
| Exhaustive unions | Thinktecture now; rip out for native C# unions on .NET 11 |
| Auth | ASP.NET Core Identity + OIDC; Zitadel deferred to v2 |
| Docs format | MDX (needed for live-view components) |
| Message overlay depth | Shallow in v1 (attach + display + lens); versioning/import in v2 |
| Embeds | Deferred to v2 (live iframe of share-tokened view) |
| Realtime | Deferred to v2; presence + soft-locking may suffice |
| Canvas placement | Manual primary, auto-layout as assist |
| MDX editor | CodeMirror, no preview pane in v1 |
| Message node shape | Horizontal pill/lozenge |
| Build approach | SDD phases in dependency order (Approach A) |

---

## 15. Deliberately deferred (v2)

- Real-time collaboration (Yjs/CRDT)
- Living embeds in external tools
- REST API / SDK
- Deployment/networking diagram types
- Version timeline UI
- Team RBAC / SSO / Zitadel
- Schema versioning + breaking-change detection
- AsyncAPI/OpenAPI/Protobuf import
- Drift detection
- Deep AI authoring (model mutations from chat)
