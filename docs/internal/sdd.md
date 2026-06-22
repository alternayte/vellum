# Vellum — System Design Document

> Status: **Draft v0.1**. Companion to `PRD.md`. Scope: **full-stack**, v1-focused, with v2 hooks called out. License: MIT.

---

## 1. Scope & approach

This document fixes the *how*. The product, principles, and conceptual model are in the PRD (`PRD.md`); this commits the physical design for all features described therein.

**PRD coverage:** this SDD addresses the full v1 scope (PRD §8, phases 0–4): meta-model & event-sourcing kernel, C4 modelling canvas, docs with live embeds, drafts/review/merge, shallow message overlay, MCP query & export, auth, and self-host deployment. **Out of scope for this SDD** (deferred to a v2 SDD): real-time multiplayer/CRDT, living embeds in external tools, REST API/SDK, deployment/networking diagram types, version-timeline UI, team RBAC/SSO, schema versioning & breaking-change detection, AsyncAPI/OpenAPI/Protobuf import, and drift detection.

**Design constraints:**
- **Single deployable, single datastore.** Self-hosters must run one container + Postgres — no Redis, no message broker, no separate IdP.
- **Event-sourced with branch/merge.** The draft/review/merge workflow requires append-only history with structural diffing; this is the defining technical problem.
- **TypeScript frontend, .NET backend.** The PRD recommends end-to-end TypeScript but notes model-heavy domain logic and the maturity of .NET's identity/OIDC stack; this SDD commits to .NET for the backend with a React SPA frontend (see §3 alternatives).
- **Solo/small-team contributor profile (v1).** Optimistic concurrency over the single project stream is sufficient; real-time CRDT is a v2 concern.
- **MIT license.** All dependencies must be MIT-compatible; no AGPL or proprietary runtime dependencies.

Guiding constraints carried from the PRD: model is the single source of truth (diagrams/docs are projections), DB-as-truth but fully serializable, event-sourced, lean and self-hostable, package-by-feature with no ceremony. The defining net-new problem versus a conventional ES app is **branch/merge of the whole model** (drafts), which drives the aggregate boundary.

---

## 2. Architecture at a glance

A **single-deployable modular monolith**: one .NET process serves the HTTP API, the MCP endpoint, background workers, and the built static SPA. One PostgreSQL instance is the only required dependency. This is the leanest thing that satisfies hosted + self-hostable.

```
┌────────────────────────────────────────────────┐
│  .NET 10 process                                 │
│  ┌──────────┐ ┌──────────┐ ┌──────────────────┐ │
│  │ HTTP API │ │ MCP (in- │ │ Background workers│ │
│  │ (minimal │ │ process) │ │ (outbox, async    │ │
│  │  APIs)   │ │          │ │  projections)     │ │
│  └────┬─────┘ └────┬─────┘ └─────────┬─────────┘ │
│       └────────────┴─────── domain ──┘           │
│                     │                            │
│              serves static SPA (React)           │
└─────────────────────┬────────────────────────────┘
                       │
                 ┌─────┴─────┐
                 │ PostgreSQL │  (event store + read models + docs)
                 └───────────┘
```

Frontend is a separate build artifact (React SPA) served by the same process; it talks to the API over an OpenAPI-generated client.

**Communication patterns:**

| Caller → Target | Transport | Sync/Async | Rationale |
|-----------------|-----------|------------|-----------|
| SPA → HTTP API | HTTP/JSON (OpenAPI) | Sync | Standard request/response; latency-sensitive UI. |
| HTTP API → Domain | In-process method call | Sync | Monolith; no network hop. |
| MCP → Domain | In-process method call | Sync | Same process; MCP SDK routes to domain services directly. |
| Domain → Event store | Postgres transaction | Sync | Atomic append of events + snapshot + outbox. |
| Domain → Inline projections | Same Postgres transaction | Sync | Must be immediately consistent for canvas reads. |
| Outbox dispatcher → Async projections | Postgres `LISTEN/NOTIFY` + polling | Async | Decoupled; eventually consistent; allows the write transaction to commit without waiting for heavy projections. |
| Background workers → Postgres | `SELECT … FOR UPDATE SKIP LOCKED` | Async | Single-runner election without external coordination. |

---

### 2.1 Glossary

Terms introduced in this SDD that are not defined in the PRD:

| Term | Definition |
|------|-----------|
| **Inline projection** | A read-model update executed within the same database transaction as the event append, guaranteeing immediate consistency. |
| **Async projection** | A read-model update executed by a background worker after the event is committed, providing eventual consistency. |
| **Gap-safe** | The property that async projections never read events from uncommitted transactions, enforced via the `xid` guard (`pg_snapshot_xmin`). |
| **Upcasting** | Transforming a stored event's JSON payload from an older version to the current version at read time, without mutating the stored data. |
| **Snapshot (stream)** | The current folded state of all events in a stream, stored as a JSONB column on the `streams` row, so loading never requires replaying history. |
| **Outbox** | A table written atomically with domain events; a background worker polls it to trigger side effects (notifications, async projections), guaranteeing at-least-once delivery without distributed transactions. |
| **Decide/Evolve** | The two pure functions of the event-sourcing pattern: `Decide(state, command) → events` validates and produces events; `Evolve(state, event) → state` folds an event into state. |
| **Closed union** | A discriminated union type where all variants are known at compile time; adding a variant causes a build failure until all consumers handle it. |
| **Transaction decorator** | A Scrutor-registered decorator that wraps every command handler, committing events + inline projections + outbox rows in one Postgres transaction. |

PRD-defined terms (Element, Relationship, View, Draft, Flow, Message, Schema, Doc, Layout, ADR, Workspace, Project) are used with the same meaning as in `PRD.md` §6.

---

## 3. Technology stack

### 3.1 Alternatives considered

| Approach | Pros | Rejected because |
|----------|------|-----------------|
| **End-to-end TypeScript (Next.js + Prisma)** | Shared front/back types; PRD's initial recommendation; MDX and Yjs are JS-native. | The domain is model-heavy with complex branching/merge logic — C#'s type system (closed unions, exhaustive matching) catches more errors at compile time. Prisma's migration story is weaker than EF Core for an event-sourced schema. The JS type-sharing benefit is replaced by OpenAPI codegen. |
| **Go single-binary backend** | Single static binary; strong concurrency; simple deployment. | Loses type sharing entirely; needs a JS sidecar for MDX rendering; no mature equivalent of EF Core for migrations; Yjs integration for v2 realtime requires a JS bridge — two runtimes to maintain. |
| **.NET backend + React SPA (chosen)** | Mature identity/OIDC, strong type system for domain logic, EF Core migrations, single-process deployment with embedded SPA, OpenAPI codegen bridges the type gap. | Requires two languages (C# + TypeScript); MDX rendering must be client-side (acceptable — it's user content viewed in the browser anyway). |

### 3.2 Stack

- **Runtime/language:** .NET 10 (stable, GA Nov 2025) / C# 14 / EF Core 10. PostgreSQL 17.
- **Exhaustiveness:** `Thinktecture.Runtime.Extensions` (MIT, v9.x) for compile-time-exhaustive `Switch`/`Map` over event/command unions today. Migrate to native C# `union` types when C# 15 / .NET 11 reaches GA (~Nov 2026) — a mechanical swap.
- **Web:** ASP.NET Core minimal APIs, first-party OpenAPI document generation (`Microsoft.AspNetCore.OpenApi`).
- **Frontend:** React 19 SPA (Vite 6.x) · TanStack Query v5 · OpenAPI codegen (`@hey-api/openapi-ts` v0.x) for the typed client · React Flow v12 (MIT) for the canvas · `@mdx-js/mdx` v3 rendered client-side.
- **DI/decoration:** Scrutor (MIT, v5.x) for the transaction decorator.
- **Auth:** ASP.NET Core Identity (cookie sessions) + external OIDC (GitHub/Google). Zitadel deferred to v2.
- **MCP:** in-process via the C# MCP SDK (`ModelContextProtocol` NuGet, v0.x).
- **Build/dev:** `dotnet` CLI, `dotnet watch`, `justfile` task runner, `docker-compose` for Postgres, Testcontainers for tests, `appsettings.json` + env-var overrides for config.

All dependencies are MIT or Apache-2.0 licensed. React Flow v12 switched to MIT (from a previously mixed license). No AGPL or proprietary runtime dependencies.

---

## 4. Solution structure (package-by-feature)

No mediator, no generic repository, no AutoMapper. Vertical slices: each feature is one file holding its command/query + validator + handler + endpoint. A single transaction decorator (via Scrutor) wraps command handlers to commit events, inline projections, and outbox atomically.

```
src/[Product]/
  Program.cs                 # composition root
  Kernel/                    # event store, projections, merge engine, Result, validation, outbox
  Modules/
    Modelling/               # the C4 model — event-sourced (elements, relationships, messages)
    Drafts/                  # branch/review/merge over the model stream
    Views/                   # saved projections + layout (NOT event-sourced)
    Flows/                   # ordered narratives over a view
    Docs/                    # MDX docs, ADRs, spaces (NOT in the model stream)
    Schemas/                 # message contracts (overlay)
    Identity/                # ASP.NET Core Identity + external OIDC
    Workspaces/              # tenancy: workspaces, projects, membership, share links
  Workers/                   # async projection host, outbox dispatcher
  ClientApp/                 # React SPA
tests/[Product].Tests/       # aggregate, integration, convention, endpoint tests (Testcontainers)
```

DbContext-per-module, each mapped to its own Postgres schema (`es`, `modelling`, `views`, `docs`, `schemas`, `identity`, `workspaces`).

---

## 5. Event-sourcing kernel

Mechanics follow a proven hand-rolled pattern (snapshot streams, decide/evolve, inline + gap-safe async projections, upcasting). What differs from a typical tracker is the **aggregate boundary** (§6).

### 5.1 Storage

```sql
-- schema: es
create table es.streams (
  stream_id    uuid primary key,
  stream_type  text not null,
  version      int  not null,
  state        jsonb not null,                 -- current snapshot
  created_at   timestamptz not null default now(),
  updated_at   timestamptz not null default now()
);

create table es.events (
  stream_id       uuid not null references es.streams(stream_id),
  version         int  not null,               -- per-stream, optimistic concurrency
  global_position bigint generated always as identity,
  event_type      text not null,               -- e.g. 'modelling.element.added.v1'
  payload         jsonb not null,
  metadata        jsonb not null,              -- actor, correlation, draft_id, merge marker
  occurred_at     timestamptz not null default now(),
  xid             xid8 not null default pg_current_xact_id(),
  primary key (stream_id, version)
);
```

### 5.2 Write path

`Decide(state, command) -> events` (pure, validates against current state). `Evolve(state, events) -> newState` (pure fold). Events + new snapshot written atomically with an optimistic-concurrency check `where version = :expected`. Loading reads the single snapshot row, never replays history.

Commands and events are modelled as **closed unions** (Thinktecture), so `Decide`/`Evolve`/upcasters are exhaustive `Switch`/`Map` — adding an event variant fails the build until every handler is updated.

### 5.3 Projections

- **Inline** (same transaction as append): read models that must be immediately consistent after a command — the canvas read models. The transaction decorator commits events + inline projections + outbox together.
- **Async** (background host): heavier or eventually-consistent read models. Each tracks position via a `checkpoints` row; the host uses `select … for update skip locked` for single-runner election and a `pg_snapshot_xmin` xid guard to never read uncommitted events; `listen/notify new_events` wakes it after each commit.

### 5.4 Upcasting

Event type names are versioned (`…added.v2`). A declarative registry maps a CLR event type to its current version + an ordered upcaster chain that mutates the stored `JsonNode` at read time. Old events are never migrated.

---

## 6. Branching & merge (the core)

### 6.1 Aggregate boundary

**One branchable stream per (project, branch).** The whole C4 model for a project is the aggregate, so a draft is "a branch of one stream" and merge is one coherent operation. This is deliberately coarser than per-entity aggregates; the snapshot-loading pattern removes the long-stream cost, and per-project edit concurrency is low (optimistic in v1, Yjs in v2). Element/relationship IDs are random `uuid`s assigned on creation and **persist across branches** — that stable identity is what makes three-way matching possible.

### 6.2 Drafts

A draft creates a new stream seeded from `main`'s snapshot at the fork point, recording `fork_point` (main's version at fork). Draft edits append to the draft stream only.

```sql
-- schema: drafts
create table drafts.drafts (
  draft_id     uuid primary key,
  project_id   uuid not null,
  stream_id    uuid not null,                  -- the draft's es stream
  base_stream  uuid not null,                  -- main's stream
  fork_point   int  not null,                  -- main version at fork
  status       text not null,                  -- open | merged | abandoned
  created_by   uuid not null,
  created_at   timestamptz not null default now()
);
```

### 6.3 Review

The changeset is a **structural diff** between `base@fork_point` and `draft@head`, presented per element/relationship as added / removed / modified (field-level). Comments attach to a draft or to individual changed entities.

### 6.4 Merge (three-way)

Three states by entity id: `base = main@fork_point`, `ours = main@current`, `theirs = draft@head`.

| base→ours | base→theirs | result |
|-----------|-------------|--------|
| unchanged | changed     | take theirs (auto) |
| changed   | unchanged   | keep ours (auto) |
| changed (same) | changed (same) | take either (auto) |
| changed (differently) | changed (differently) | **conflict** |
| deleted   | modified    | **conflict** (delete/modify) |
| added id (distinct) | added id (distinct) | keep both |

Non-conflicting changes auto-merge; genuine conflicts are surfaced for manual resolution and block completion until resolved. On success, the resolved changes are re-emitted as events on `main` within one transaction (a merge commit), tagged in metadata with `draft_id` + a merge correlation id, so projections update per-entity and history shows the provenance. The draft is marked `merged`.

### 6.5 Decision records

An ADR is a doc (Docs module) linked to a `draft_id` and the entity ids it touched, giving the navigable chain **decision → draft → affected elements → live diagram**.

---

## 7. Domain model → physical schema

### 7.1 The model (projected read models, `modelling` schema)

Maintained by inline projections off the model stream. The canvas reads these directly — never event replay.

```sql
create table modelling.elements (
  id          uuid primary key,
  project_id  uuid not null,
  branch      uuid not null,          -- stream_id (main or a draft)
  kind        text not null,          -- actor|system|app|store|component
  name        text not null,
  description text,
  technology  text,
  owner_id    uuid,
  status      text not null default 'current', -- current|planned|deprecated|removed
  parent_id   uuid,                   -- containment → C4 zoom
  tags        text[] not null default '{}'
);

create table modelling.relationships (
  id          uuid primary key,
  project_id  uuid not null,
  branch      uuid not null,
  from_id     uuid not null,
  to_id       uuid not null,
  label       text,
  technology  text,
  message_id  uuid                    -- optional → message overlay
);
```

(Read models are branch-scoped so the canvas can render any draft as `base + draft` cheaply.)

### 7.2 Event set (illustrative, a closed union)

`ElementAdded`, `ElementRenamed`, `ElementReparented`, `ElementRetagged`, `ElementStatusChanged`, `ElementRemoved`, `RelationshipAdded`, `RelationshipChanged`, `RelationshipRemoved`, `MessageAttached`, `MessageDetached`. Drafts append the same event types to their own stream; merge re-emits the resolved subset onto main.

### 7.3 Views, layout, flows

- **Views** (`views` schema): a saved projection — root element, visible-element set, active filters/lens, active flow. Plain mutable rows.
- **Layout** kept **out of the event log** — a mutable per-view table (`view_id, element_id, x, y`) plus edge routing hints. Position churn must not bloat or pollute the semantic history.
- **Flows**: ordered steps referencing element/relationship ids, scoped to a view.

### 7.4 Messages & schemas (overlay, shallow in v1)

`schemas.messages` (event/command node + `schema_ref`) and `schemas.schemas` (contract blob + format). v1 = attach + display + producer/consumer wiring + canvas lens. Versioning, breaking-change detection, and import are v2.

### 7.5 Docs

`docs` schema: MDX content rows organised into spaces, attachable to an element or standalone, with cross-links and embedded-view references. Docs are **not** part of the model event stream; optional revision history is a simple revisions table.

### 7.6 Schema evolution strategy

- **Event store:** events are immutable; schema changes are handled by upcasting (§5.4). New event versions get new type names (`element.added.v2`); old rows are never migrated.
- **Read-model tables (projections):** evolved via EF Core migrations. Since read models are fully rebuildable from events, a breaking migration can drop and recreate a projection table and replay from the event log. The async projection host detects a reset checkpoint and rebuilds automatically.
- **Non-event tables (views, layout, docs, identity, workspaces):** standard EF Core migrations with forward-only scripts. Blue-green deployments apply migrations before swapping traffic; the prior version's code must tolerate additive schema changes (new nullable columns). Destructive column removals are a two-release process: (1) stop writing, (2) next release drops the column.

### 7.7 Data volume & growth projections

Architecture models are structurally small. Expected ranges for a single project:

| Entity | Typical count | Large model |
|--------|--------------|-------------|
| Elements | 20–100 | 500 |
| Relationships | 30–200 | 1,000 |
| Events (per project stream) | 200–2,000 | 20,000 |
| Views | 5–20 | 100 |
| Docs | 10–50 | 500 |

Per workspace (multi-project): 5–20 projects typical, ~50 max for a large org.

**Storage estimate:** a 500-element project with 20,000 events ≈ 50 MB (events JSONB). 50 such projects ≈ 2.5 GB. Postgres handles this comfortably on a single node without partitioning.

**Growth triggers:**
- If a single project stream exceeds ~100,000 events: consider snapshot-only loading (already the default) and optional event archival (move old events to a cold table).
- If total DB size exceeds ~50 GB: standard Postgres operational scaling (larger instance, connection pooling). Sharding is not anticipated for v1's target audience.

### 7.8 Backup & recovery

- **Strategy:** PostgreSQL `pg_dump` (logical) for self-hosters; managed backups (automated daily snapshots + WAL archiving for PITR) for the hosted offering.
- **RPO:** ≤ 1 hour for hosted (WAL archiving); self-hosters configure their own schedule.
- **RTO:** < 30 minutes for hosted (restore from snapshot + replay WAL). Self-hosters: restore from `pg_dump`.
- **Model export:** the YAML/JSON serialisation (§13) provides an application-level backup independent of Postgres, usable for data portability and disaster recovery.
- **Testing:** backup restore is validated as part of the hosted deployment runbook (quarterly restore-to-staging test).

---

## 8. API

OpenAPI-first. Minimal-API endpoints per vertical slice; ASP.NET emits the OpenAPI document; the frontend regenerates its typed client (Hey API) from it. Commands return `Result` (typed success/validation/conflict — a union), mapped to appropriate HTTP status. Optimistic-concurrency failures surface as `409` for the client to reload.

### 8.1 Versioning

v1 ships a single API version with no version prefix (paths are `/api/projects/{id}/elements`, not `/api/v1/…`). When breaking changes are needed (v2+), URL-prefix versioning (`/api/v2/…`) is used with the prior version maintained for one release cycle. The OpenAPI document includes a version field; the generated client pins to it.

### 8.2 Authentication & authorisation per endpoint

All endpoints require authentication (httpOnly cookie session) except:
- `GET /api/share/{token}/*` — share-link endpoints, authenticated by the opaque token.
- `GET /health`, `GET /ready` — unauthenticated probes.

Authorisation is role-based at the workspace/project level:

| Role | Read model | Write model | Manage drafts | Manage workspace |
|------|-----------|-------------|--------------|-----------------|
| Viewer | Yes | No | No | No |
| Editor | Yes | Yes | Yes | No |
| Owner | Yes | Yes | Yes | Yes |

`403 Forbidden` is returned when authenticated but lacking the required role. `401 Unauthorized` when the session cookie is missing or expired.

### 8.3 Rate limiting

v1 applies a simple per-user rate limit via ASP.NET Core's built-in rate-limiting middleware:
- **Read endpoints:** 200 requests/minute per user.
- **Write endpoints:** 60 requests/minute per user.
- **Share-link endpoints:** 100 requests/minute per token (public, so rate-limited by token to prevent abuse).

Exceeded limits return `429 Too Many Requests` with a `Retry-After` header. No per-workspace quotas in v1.

### 8.4 Pagination

List endpoints use **cursor-based pagination** (opaque cursor encoded from `(sort_key, id)`):
- Default page size: 50. Max: 200.
- Response shape: `{ items: T[], cursor: string | null }`. `cursor = null` means no more pages.
- Sort: each list endpoint defines its default sort (e.g., elements by name, events by global_position). Additional sort options are endpoint-specific.
- Filtering: query-string parameters per endpoint (e.g., `?kind=app&status=current`). No generic filter grammar in v1.

### 8.5 Idempotency

- **Element/relationship creation** uses client-supplied UUIDs (`id` in the request body). Replaying a create with the same ID and matching fields is a no-op (returns the existing entity). Mismatched fields return `409`.
- **Draft merge** is naturally idempotent — merging an already-merged draft returns success with the existing merge result.
- **Other mutations** (rename, retag, etc.) are idempotent by nature (setting a field to a value is the same whether applied once or twice). Optimistic concurrency (`If-Match` version header) prevents lost updates.

### 8.6 Error format

All errors follow a consistent envelope:

```json
{
  "type": "validation_error | conflict | not_found | forbidden | rate_limited",
  "title": "Human-readable summary",
  "detail": "Specific explanation",
  "errors": [
    { "field": "name", "message": "Required" }
  ]
}
```

Maps to HTTP status: `400` (validation), `404`, `403`, `409` (conflict/concurrency), `429`, `500` (unexpected).

### 8.7 Representative endpoint contracts

Full contracts live in the auto-generated OpenAPI spec. Representative examples for the core modelling API:

**`POST /api/projects/{projectId}/elements`** — Create element
```
Request:  { id: uuid, kind: string, name: string, description?: string, technology?: string, parentId?: uuid, status?: string, tags?: string[] }
Headers:  Cookie (session)
Response: 201 { id, kind, name, description, technology, ownerId, status, parentId, tags }
Errors:   400 (validation), 401, 403, 409 (duplicate id with different fields)
```

**`GET /api/projects/{projectId}/elements`** — List elements
```
Query:    ?kind=&status=&parentId=&cursor=&limit=
Response: 200 { items: Element[], cursor: string | null }
```

**`POST /api/projects/{projectId}/drafts`** — Create draft
```
Request:  { id: uuid, name: string }
Response: 201 { draftId, projectId, streamId, baseStream, forkPoint, status, createdBy, createdAt }
```

**`POST /api/projects/{projectId}/drafts/{draftId}/merge`** — Merge draft
```
Request:  { resolutions?: { entityId: uuid, pick: "ours" | "theirs" | "edit", value?: object }[] }
Response: 200 { merged: true, conflictsRemaining: 0 }
Errors:   409 (unresolved conflicts remain), 400 (draft not open)
```

---

## 9. Frontend architecture

- **Data:** React Query over the generated client. Read models drive the canvas; mutations post commands and invalidate queries.
- **Canvas:** React Flow with custom C4-styled nodes. We build, on top of it:
  - **Semantic zoom / level-of-detail** — swap node detail as you drill context → app → component (React Flow does not provide this).
  - **Auto-layout** via elkjs (or dagre) for initial placement; manual moves persist to the layout table.
  - **Lenses** — metadata, status colours, flows, and the message overlay are all client-side filters over one loaded model.
- **Docs:** MDX compiled and rendered **client-side**; component set is **whitelisted/sandboxed** (user-authored MDX is untrusted in a multi-tenant app). A `<LiveView/>` MDX component embeds a current view from the same model.
- **Visual quality is a tracked requirement** — design-system discipline (restrained palette, soft shadows, type hierarchy inside nodes, tech icons, clean edge routing) to meet/beat IcePanel. Detailed tokens belong in the frontend build, guided by the design skill.

---

## 10. Auth & multi-tenancy

- **v1 auth:** ASP.NET Core Identity with httpOnly cookie sessions (safer than tokens-in-JS for a same-origin SPA) + external OIDC providers (GitHub/Google) via Identity's built-in support. No separate IdP to self-host. **Zitadel is a v2 option** for SSO/org management.
- **Tenancy:** row-level `workspace_id` on tenant-scoped tables (schema-per-*module* is organisational, not per-tenant). Workspace → Project → membership (owner/editor/viewer for v1).
- **Share links:** opaque tokens granting public read-only access to a view or doc, independent of user accounts.

---

## 11. Background jobs & outbox

Hand-rolled on Postgres (no Hangfire): an outbox table and job tables drained by a worker using `select … for update skip locked`, the same primitive as the async projection host. v1 jobs: outbox dispatch, async projections. v2 adds imports and drift detection.

### 11.1 Retry & failure handling

- **Outbox dispatch:** retried up to 5 times with exponential backoff (1s, 5s, 25s, 2m, 10m). After 5 failures, the message is moved to a `dead_letters` table with the error details and a `failed_at` timestamp. An admin query surfaces dead letters; v1 does not auto-alert (see §19 observability for the metric).
- **Async projections:** if a single event fails to project, the projection host logs the error, increments a per-projection error counter, and skips the event after 3 retries. The projection falls behind; the health check (§19.6) reports degraded. Manual intervention: fix the bug, reset the checkpoint, rebuild.
- **Merge:** merge is a single synchronous transaction — no long-running lifecycle. If the merge transaction fails (e.g., concurrent main-stream append), the client receives a `409` and retries. No server-side retry.

### 11.2 Timeout budget

| Operation | Timeout | Behaviour on timeout |
|-----------|---------|---------------------|
| HTTP request (end-to-end) | 30s (ASP.NET default) | `504 Gateway Timeout` if behind a reverse proxy; otherwise connection reset. |
| Database query | 15s (`CommandTimeout`) | Throws; command handler returns `500`. |
| Outbox dispatch (per message) | 10s | Counts as a failure; retried per §11.1. |
| Async projection (per event) | 5s | Counts as a failure; retried per §11.1. |

For v1's architecture-tool workload (small models, low concurrency), these timeouts are generous. The merge transaction is the heaviest single operation; even a 500-element three-way merge completes well under 1s in testing.

---

## 12. MCP

In-process via the C# MCP SDK, exposed by the same .NET app over the same domain services. Read tools first (query the model: "what depends on X?", "who owns Y?"); authoring tools (agent edits the model through commands) follow. A canonical export (§13) backs `llms.txt`-style discovery.

---

## 13. Serialization & export

The model serialises to a canonical YAML/JSON form with **stable key ordering** for clean diffs and round-trip fidelity. v1 ships export (own-your-data + AI/MCP). Layout is serialised separately so semantic exports stay clean.

---

## 14. Realtime (v2)

Live multiplayer uses Yjs as the **in-session** transport/merge substrate; the backend stays authoritative. A settled session persists as a merge commit onto the stream (§6.4), so the ES model never sees per-keystroke churn. Presence + soft-locking is the cheaper fallback if full CRDT proves unwarranted for an architecture tool's lower edit concurrency.

---

## 15. PostgreSQL conventions

Idiomatic throughout: `snake_case` tables/columns via `EFCore.NamingConventions` (no quoted PascalCase identifiers), lower_snake schema-per-module, `timestamptz` for all timestamps, `uuid` keys, `bigint generated always as identity` for ordering columns, `jsonb` for payloads/snapshots/state. Index `events(global_position)`, `events(stream_id, version)`, and branch-scoped read-model lookups.

---

## 16. Deployment & self-host

Single container image (.NET app with the SPA built in) + Postgres, wired by `docker-compose`. `justfile` for dev tasks (`just up`, `just run`, `just test`, `just migrate`). Config via `appsettings.json` with environment-variable overrides for everything operationally relevant (connection string, OIDC client IDs/secrets, base URL) so local-dev and self-host reconfigure without rebuilds.

### 16.1 Deployment topology

**Self-hosted (default):** single container + single Postgres instance, same machine or separate. No load balancer, no clustering. Suitable for teams up to ~50 users.

**Hosted offering:** the same container image behind a load balancer (sticky sessions for cookie auth), connecting to a managed Postgres instance (e.g., AWS RDS / Cloud SQL). Horizontal scaling is straightforward because the app is stateless (all state in Postgres); background workers use `SELECT … FOR UPDATE SKIP LOCKED` for leader election so multiple instances don't conflict. DNS-based service discovery.

```
                    ┌──────────────┐
  Users ──HTTPS──▶  │ Load Balancer │  (sticky sessions)
                    └──────┬───────┘
                    ┌──────┴───────┐
              ┌─────┤  App (N=1–3) ├─────┐
              │     └──────────────┘     │
              │     ┌──────────────┐     │
              └─────┤  Managed PG  ├─────┘
                    └──────────────┘
```

v1 targets a single cloud region. Multi-region is a v2+ concern.

---

## 17. Testing

Testcontainers-backed Postgres (no manual DB setup). Test types: **aggregate** (decide/evolve given events → expected events/state), **merge** (three-way scenarios incl. every conflict case in §6.4), **integration** (command → projection consistency), **convention** (enforce package-by-feature + naming rules), **endpoint** (API contract). The merge engine and the gap-safe async projections get the heaviest coverage — they're the riskiest code.

---

---

## 18. Error handling & resilience

### 18.1 Error taxonomy

| Category | HTTP status | Retry? | Example |
|----------|------------|--------|---------|
| **Validation** | 400 | No (fix input) | Missing required field, invalid element kind. |
| **Not found** | 404 | No | Element/project/draft ID doesn't exist. |
| **Forbidden** | 403 | No | User lacks role for this workspace. |
| **Conflict (optimistic concurrency)** | 409 | Yes (reload state, re-apply) | Another session modified the same stream version. |
| **Conflict (business)** | 409 | No (resolve manually) | Merge conflicts; duplicate idempotent create with different fields. |
| **Rate limited** | 429 | Yes (after `Retry-After`) | Per-user rate exceeded. |
| **Transient (infra)** | 503 | Yes (backoff) | Postgres connection lost, timeout. |
| **Unexpected** | 500 | No (report bug) | Unhandled exception. |

### 18.2 Failure modes

The system has a single infrastructure dependency (PostgreSQL). Failure scenarios:

| Failure | Impact | Behaviour |
|---------|--------|-----------|
| **Postgres down** | Complete outage | API returns `503`. The app's readiness probe fails; the load balancer stops routing. Inline and async projections stall. Recovery: automatic when Postgres recovers (connection pool reconnects). |
| **Postgres slow** | Degraded latency | Queries hit `CommandTimeout` (15s) and return `500`. The health check latency metric rises; alerting fires (§19.3). |
| **Postgres disk full** | Write failures | Event appends fail; commands return `500`. Read-only operations continue. Alert on disk utilisation. |
| **App process crash** | Temporary outage | Container orchestrator restarts the process. In-flight requests fail; clients retry. Outbox/async projections resume from checkpoint on restart. |
| **Async projection bug** | Stale read model | Projection falls behind; health check reports degraded (§19.6). Canvas still works (inline projections are unaffected). Fix bug, reset checkpoint, rebuild. |

### 18.3 Graceful degradation

- If async projections fall behind, the core canvas experience (powered by inline projections) is unaffected. Features that depend on async read models (e.g., search indexes, cross-project queries in v2) degrade gracefully: the UI shows a "data may be stale" indicator.
- If the outbox is backed up, side effects (e.g., notifications in v2) are delayed but never lost. Core write+read operations are unaffected.
- Dead-lettered outbox messages are surfaced via a metrics counter and (in v2) an admin dashboard.

Circuit breakers and bulkheads are not needed in v1: the monolith has a single dependency (Postgres), and Postgres is either available or it isn't. These patterns are relevant when v2 introduces external integrations (AsyncAPI import, drift detection against live infrastructure).

---

## 19. Observability & operations

### 19.1 Structured logging

- **Framework:** .NET's built-in `ILogger` with Serilog for structured JSON output to stdout (12-factor; container orchestrators capture stdout).
- **Levels:** `Debug` (detailed domain logic, off in production), `Information` (command executed, projection updated, merge completed), `Warning` (retry, slow query, rate limit hit), `Error` (unhandled exception, dead letter, projection failure).
- **Correlation:** every HTTP request gets a `CorrelationId` (from `X-Correlation-Id` header or generated). All log entries within the request include it. Background workers generate their own correlation IDs per batch.
- **PII:** user IDs are logged; email addresses and names are **not** logged. OIDC tokens are never logged.

### 19.2 Metrics (SLIs)

Exposed via the .NET `System.Diagnostics.Metrics` API, scraped by Prometheus (or compatible):

| Metric | Type | SLI? |
|--------|------|------|
| `http_request_duration_seconds` (by endpoint, status) | Histogram | Yes — p95 < 200ms for reads, < 500ms for writes |
| `http_requests_total` (by endpoint, status) | Counter | Yes — error rate < 1% |
| `es_events_appended_total` | Counter | No |
| `projection_lag_events` (by projection name) | Gauge | Yes — < 100 events behind |
| `outbox_pending_count` | Gauge | Yes — < 50 messages pending |
| `dead_letter_count` | Counter | No (but alerted on) |
| `db_connection_pool_active` | Gauge | No |

### 19.3 Alerting

| Alert | Condition | Severity |
|-------|-----------|----------|
| High error rate | `http_requests_total{status=~"5.."}` > 5% over 5 min | Page |
| High latency | `http_request_duration_seconds` p95 > 1s over 5 min | Page |
| Projection lagging | `projection_lag_events` > 500 for 10 min | Ticket |
| Outbox backed up | `outbox_pending_count` > 100 for 10 min | Ticket |
| Dead letters | `dead_letter_count` increase > 0 | Ticket |
| Disk usage | Postgres disk > 80% | Ticket |
| Disk usage critical | Postgres disk > 90% | Page |

### 19.4 Distributed tracing

v1 is a monolith with a single dependency — distributed tracing adds limited value. However, OpenTelemetry is wired up for future use:
- The .NET app exports traces via OTLP (configurable exporter endpoint via env var, disabled by default for self-hosters).
- HTTP requests create a root span; EF Core queries and outbox dispatch create child spans.
- Sampling: 100% in dev, 10% in production (configurable).

### 19.5 Runbooks

Runbook entries for each page-level alert are maintained alongside the deployment config. At minimum, each runbook documents:
1. What the alert means (which SLI is breached).
2. First checks (Postgres connectivity, recent deploys, disk/CPU, logs for the correlation ID).
3. Common causes and resolutions.
4. Escalation path.

Runbook content is authored as the alerting rules are implemented (not pre-written as a document exercise).

### 19.6 Health checks

| Probe | Path | Checks | Failure response |
|-------|------|--------|-----------------|
| **Liveness** | `GET /health` | App process is running, not deadlocked | `503` → orchestrator restarts the container |
| **Readiness** | `GET /ready` | Postgres connection pool healthy, migrations applied, async projections within lag threshold | `503` → load balancer stops routing traffic |

Both probes are unauthenticated. The readiness probe fails if any async projection is > 500 events behind (configurable), so a projection bug triggers traffic drain rather than serving stale data.

---

## 20. Security

### 20.1 Threat model

| Threat | Attack surface | Mitigation |
|--------|---------------|------------|
| **XSS via MDX** | User-authored MDX content rendered client-side | Component whitelist: only approved components (`<LiveView/>`, standard HTML elements) are rendered. MDX compilation strips unknown components. CSP headers restrict inline scripts. |
| **CSRF** | Cookie-based auth on mutating endpoints | ASP.NET Core's anti-forgery tokens on all POST/PUT/DELETE. SameSite=Strict on session cookies. |
| **Enumeration via share links** | Opaque token in URL | Tokens are 256-bit random (cryptographically secure), URL-safe base64. Rate-limited. Not guessable. |
| **OIDC token theft** | Session hijacking | httpOnly, Secure, SameSite=Strict cookies. Short session lifetime (configurable, default 7 days). Server-side session revocation. |
| **SQL injection** | API input | All queries via EF Core parameterised queries. No raw SQL interpolation. Convention tests enforce this. |
| **Privilege escalation** | Workspace role bypass | Authorisation checked at the command handler level (not just the endpoint). Convention tests verify every command handler checks workspace membership. |
| **Denial of service** | Public endpoints (share links, auth) | Rate limiting (§8.3). Request size limits (1 MB body default). Postgres connection pooling. |

### 20.2 Secrets management

- **Runtime secrets** (Postgres connection string, OIDC client secrets): passed via environment variables. Never in `appsettings.json` committed to source control. `appsettings.json` contains only non-secret defaults.
- **Hosted offering:** secrets stored in the cloud provider's secret manager (e.g., AWS Secrets Manager, GCP Secret Manager) and injected as env vars at deploy time.
- **Self-hosters:** documented in the self-host guide — set env vars in `docker-compose.yml` (which is `.gitignore`d) or via their orchestrator's secret mechanism.
- **Rotation:** OIDC client secrets rotated by re-configuring the env var and restarting. Database credentials rotated via managed-DB credential rotation (hosted) or manual rotation (self-hosted). No in-app secret caching that would require a process restart.

### 20.3 Encryption

- **In transit:** HTTPS enforced. The app sets `HSTS` headers. Self-hosters are documented to terminate TLS at a reverse proxy (nginx/Caddy). The docker-compose dev setup uses HTTP (localhost only).
- **At rest:** Postgres data-at-rest encryption is delegated to the managed DB provider (hosted) or the self-hoster's Postgres configuration. The application does not implement its own field-level encryption in v1. Event payloads and snapshots are JSONB and readable by anyone with DB access — access control is at the DB credential level.

### 20.4 Input validation

Untrusted input enters at three boundaries:
1. **HTTP API:** validated by FluentValidation (or minimal-API filters) at the endpoint layer before reaching the domain. All fields are type-checked, length-limited, and pattern-validated where relevant.
2. **MDX content:** compiled client-side with a whitelisted component set. The server stores MDX as-is but never executes it.
3. **Share link tokens:** validated as exact match against the database; no parsing or interpretation.

### 20.5 Audit trail

v1 has a natural audit trail via the event log: every model change is an event with actor, timestamp, and correlation metadata. This covers:
- Who changed what, when (event `metadata.actor`).
- Draft creation, merge, abandonment.
- Element/relationship additions, modifications, removals.

The event log is append-only and immutable — events are never deleted or mutated.

Not covered in v1 (deferred to v2): auth events (login, logout, failed login attempts), workspace membership changes, share-link creation/revocation. These will be a separate audit-event table outside the model event log.

---

## 21. Testing strategy (expanded)

Building on §17:

### 21.1 Test pyramid

| Layer | Coverage | What it deliberately doesn't cover |
|-------|----------|-----------------------------------|
| **Aggregate (unit)** | Decide/Evolve logic, business rules, all event types, edge cases | Database, HTTP, serialisation |
| **Merge (unit)** | Every row of the §6.4 conflict matrix, multi-entity merges, conflict resolution | Database (operates on in-memory snapshots) |
| **Integration** | Command → event store → inline projection → query — full vertical slice through Postgres | Frontend, external OIDC providers |
| **Convention** | Package-by-feature structure, naming rules, no raw SQL, all command handlers check auth | Business logic |
| **Endpoint** | HTTP request → response, status codes, error formats, auth enforcement | Internal domain logic (covered by aggregate tests) |
| **E2E (future)** | Happy-path flows through the UI (Playwright) — added once the SPA stabilises | Exhaustive edge cases (covered by lower layers) |

### 21.2 Performance testing

- **Tool:** k6 or `dotnet-benchmark` for targeted benchmarks.
- **Targets:** merge of a 500-element model < 1s; canvas read (100 elements) < 100ms; event append < 50ms.
- **Cadence:** run against CI on merge to main; results compared to baseline. Regression > 20% fails the build.

### 21.3 Contract testing

The OpenAPI spec is the contract. The endpoint tests validate that the actual HTTP responses match the OpenAPI document (schema validation in tests). The frontend's generated client (`@hey-api/openapi-ts`) is regenerated from the spec on every backend change — a type error in the frontend build catches contract drift.

### 21.4 Chaos / failure injection

Not planned for v1. Justification: the system has a single dependency (Postgres), and the failure modes are well-understood (§18.2). The resilience design is tested by:
- Integration tests that simulate Postgres restarts (Testcontainers stop/start).
- Merge tests covering every conflict case.
- Outbox retry tests with injected failures.

Chaos engineering (e.g., network partitions, latency injection) becomes relevant in v2 when external integrations are added.

---

## 22. Migration & rollout

### 22.1 Build order (greenfield v1)

This is a greenfield project. The migration path is the build order from PRD §8, with validation gates:

| Step | Phase | Pre-condition | Validation gate | Est. duration |
|------|-------|--------------|-----------------|---------------|
| 1 | Foundation | — | Event store passes aggregate tests; snapshot load/save round-trips; projection host processes events without gaps | 2–3 weeks |
| 2 | Modelling | Step 1 passes | Elements/relationships CRUD on canvas; semantic zoom works across 3 C4 levels; layout persists | 3–4 weeks |
| 3 | Docs | Step 2 passes | MDX editor renders; `<LiveView/>` embeds a live view; cross-linking works | 2–3 weeks |
| 4 | Planning | Step 3 passes | Draft fork → edit → review changeset → merge (incl. conflict resolution) works end-to-end; ADRs link to drafts | 3–4 weeks |
| 5 | Bridge & AI | Step 4 passes | Message overlay renders on canvas; MCP answers "what depends on X?" against a test model; YAML export round-trips | 2–3 weeks |
| 6 | Auth & multi-tenancy | Step 2 passes (parallel) | Login via OIDC; workspace isolation; share links; role enforcement | 2–3 weeks |
| 7 | Deployment | Step 6 passes | `docker-compose up` runs the full app; self-host guide validated | 1 week |

Steps 1–5 are sequential (each builds on the prior). Step 6 can run in parallel with steps 3–5.

### 22.2 Feature flags & gradual rollout

v1 is a greenfield launch — no existing users to migrate. The hosted offering launches as a closed beta:
1. **Private beta:** invite-only (shared via share links + direct accounts). All features enabled.
2. **Public beta:** open registration. Feature flags (simple config-driven, not a third-party service) gate any unstable features.
3. **GA:** remove beta flag. Feature flags retained for operational kill-switches (e.g., disable MCP endpoint if it causes load issues).

### 22.3 Rollback

- **Application rollback:** deploy the previous container image. The app is stateless; rollback is instant.
- **Database rollback:** EF Core migrations include `Down()` methods for additive changes. For destructive migrations (rare, see §7.6), the rollback plan is documented per migration. Event store data is never deleted — rolling back the app version doesn't lose events, but new event types introduced by the newer version won't be understood by the older app (upcasters only go forward). In practice, this means: don't roll back past an event-schema change without verifying that no new-format events have been appended.
- **Time estimate:** < 5 minutes for application rollback; 5–30 minutes for database rollback depending on migration complexity.

### 22.4 Post-launch schema changes

Once users exist, schema evolution follows §7.6: additive changes are safe; destructive changes are two-phase. Event upcasting (§5.4) handles event-schema evolution without migrating stored data. Read-model tables can be rebuilt from events at any time.

---

## 23. Deferred / open

- Exact draft storage tuning if a single project stream grows hot (partitioning, snapshot cadence) — revisit only if measured.
- v2: schema versioning + breaking-change detection, AsyncAPI/OpenAPI import, drift detection, deployment/network diagram types, version-timeline UI, embeds, team RBAC/SSO, Yjs realtime.
- Frontend design tokens / component library — owned by the frontend build.
