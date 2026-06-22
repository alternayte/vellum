# Phase 1a — Backend: Modelling Domain, Auth, Workspaces, Views & API

> Companion to `PRD.md`, `SDD.md`, `UI_UX.md`. Builds on Phase 0 kernel. Scope: everything the frontend needs to build against.

---

## 1. Scope

This spec covers the backend half of Phase 1 (Modelling). The frontend (React SPA, canvas, shell) is a separate spec (Phase 1b).

**In scope:**
- Identity module (ASP.NET Core Identity + OIDC)
- Workspaces module (Workspace → Project → Membership CRUD with roles)
- Modelling module (event-sourced C4 model aggregate, inline projections, read model tables)
- Views module (mutable CRUD for views + layout positions)
- API endpoints for all of the above
- Testing (aggregate, integration, endpoint, convention)
- Dev seeding and migration tooling

**Out of scope (Phase 1b or later):**
- React SPA, canvas, React Flow, design system
- Flows (Phase 1b or Phase 2)
- Docs / MDX (Phase 2)
- Drafts / review / merge (Phase 3)
- Message overlay / schemas (Phase 4)
- MCP (Phase 4)
- Rate limiting (not needed for Phase 1 dev)

---

## 2. Module Structure

Following the anthology pattern — one endpoints file per module, feature slices are command/handler files. Scrutor scans and decorates `ICommandHandler<,>` for event-sourced command handlers only. Non-event-sourced modules (Views, Workspaces) use plain handler classes called directly from endpoints — they don't implement `ICommandHandler<,>` and don't go through `TransactionBehavior`.

```
src/Vellum/
  Program.cs
  Kernel/                          # (Phase 0 — already built)
  Modules/
    Identity/
      IdentityModule.cs            — AddIdentityModule(services, config)
      IdentityEndpoints.cs         — MapGroup("/api/auth")
    Workspaces/
      WorkspacesModule.cs          — AddWorkspacesModule(services)
      WorkspaceEndpoints.cs        — MapGroup("/api/workspaces") + project/member sub-groups
      WorkspacesDbContext.cs
      CreateWorkspace.cs
      UpdateWorkspace.cs
      ListWorkspaces.cs
      CreateProject.cs
      UpdateProject.cs
      DeleteProject.cs
      ListProjects.cs
      InviteMember.cs
      RemoveMember.cs
    Modelling/
      ModellingModule.cs           — AddModellingModule(services), RegisterEvents(registry)
      ModellingEndpoints.cs        — MapGroup("/api/projects/{projectId}") + element/relationship sub-groups
      ModellingDbContext.cs         — read model tables (modelling schema)
      Model/
        ModelEvents.cs             — closed union of all modelling events
        ModelState.cs              — aggregate state
        ModelDecider.cs            — Decide(state, command) → events
        ModelProjection.cs         — IInlineProjection → writes to read model tables
      Elements/
        AddElement.cs
        UpdateElement.cs
        RemoveElement.cs
        GetElement.cs
        ListElements.cs
      Relationships/
        AddRelationship.cs
        UpdateRelationship.cs
        RemoveRelationship.cs
        GetRelationship.cs
        ListRelationships.cs
    Views/
      ViewsModule.cs
      ViewEndpoints.cs             — MapGroup("/api/projects/{projectId}/views") + layout sub-group
      ViewsDbContext.cs
      CreateView.cs
      UpdateView.cs
      DeleteView.cs
      GetView.cs
      ListViews.cs
      SaveLayout.cs
```

---

## 3. Modelling Aggregate

### 3.1 Aggregate Boundary

One event-sourced stream per project (the "main" branch — draft branches come in Phase 3). The aggregate state holds the full C4 model: all elements and relationships for that project.

Stream type: `"model"`. Stream ID: the project's main stream ID (created when the project is created).

### 3.2 Aggregate State

```
ModelState
  Elements: Dictionary<Guid, ElementState>
  Relationships: Dictionary<Guid, RelationshipState>

ElementState
  Id: Guid
  Kind: ElementKind (actor | system | app | store | component)
  Name: string
  Description: string?
  Technology: string?
  OwnerId: Guid?
  Status: ElementStatus (current | planned | deprecated | removed)
  ParentId: Guid?
  Tags: string[]

RelationshipState
  Id: Guid
  FromId: Guid
  ToId: Guid
  Label: string?
  Technology: string?
  MessageId: Guid?              — unused until Phase 4; nullable placeholder to avoid migration
```

`IAggregateState<ModelState, ModelEvent>` — `Initial` is empty dictionaries, `Evolve` folds each event.

### 3.3 Event Set (closed union)

Elements:
- `ElementAdded` — id, kind, name, description?, technology?, ownerId?, status, parentId?, tags[]
- `ElementRenamed` — elementId, name
- `ElementDescriptionChanged` — elementId, description?
- `ElementTechnologyChanged` — elementId, technology?
- `ElementOwnerChanged` — elementId, ownerId?
- `ElementReparented` — elementId, parentId?
- `ElementStatusChanged` — elementId, status
- `ElementRetagged` — elementId, tags[]
- `ElementRemoved` — elementId

Relationships:
- `RelationshipAdded` — id, fromId, toId, label?, technology?, messageId? (unused until Phase 4)
- `RelationshipLabelChanged` — relationshipId, label?
- `RelationshipTechnologyChanged` — relationshipId, technology?
- `RelationshipRemoved` — relationshipId

Event type names follow the pattern `modelling.element.added.v1`, `modelling.relationship.removed.v1`, etc.

### 3.4 Decide Rules

**Kind containment (C4 hierarchy):**
- `actor` → no parent (top-level only)
- `system` → no parent (top-level only)
- `app` → parent must be `system`
- `store` → parent must be `system`
- `component` → parent must be `app`

**Element commands:**
- `AddElement` — validate: name required, kind valid, parent exists and satisfies containment rules (or null for top-level kinds), id not already taken. Emit `ElementAdded`.
- `UpdateElement` (PATCH semantics) — compare incoming partial fields against current state, emit only events for fields that actually changed. E.g., `{name: "New", status: "planned"}` → `ElementRenamed` + `ElementStatusChanged`. Validate: element must exist, containment rules on reparent. **`kind` is immutable after creation** — changing kind would break containment invariants (e.g., a `system` with `app` children can't become a `component`). To change kind, remove and re-add.
- `RemoveElement` — **cascade-remove**: emit `RelationshipRemoved` for every relationship referencing the element (from or to), then `ElementRemoved` for the element itself. If the element has children, recursively cascade-remove all descendants (children first, then the element). All removals emitted as individual events for clean audit trail and Phase 3 diff visibility.

**Reparent validation:** `ElementReparented` validates containment rules for the *new* parent, not just during `AddElement`. An `app` cannot be reparented to null (apps require a `system` parent), and cannot be reparented to another `app` (only `system` is valid). A `component` cannot be reparented to a `system` (only `app` is valid). Top-level kinds (`actor`, `system`) reject any non-null parent.

**Relationship commands:**
- `AddRelationship` — validate: from and to elements must exist, id not taken. Emit `RelationshipAdded`.
- `UpdateRelationship` (PATCH semantics) — same diff-and-emit pattern as elements. Emit only changed fields.
- `RemoveRelationship` — validate: must exist. Emit `RelationshipRemoved`.

### 3.5 Inline Projection

`ModelProjection` implements `IInlineProjection`. Switches on collected event types, upserts/deletes rows in the `modelling` schema tables. Runs in the same transaction as the event append (via `TransactionBehavior`).

No async projections needed in Phase 1 — all read models are inline.

---

## 4. Read Model Tables

### 4.1 Modelling Schema

```sql
-- schema: modelling
create table modelling.elements (
  id          uuid primary key,
  project_id  uuid not null,
  branch      uuid not null,          -- stream_id (main for now, draft streams in Phase 3)
  kind        text not null,          -- actor|system|app|store|component
  name        text not null,
  description text,
  technology  text,
  owner_id    uuid,
  status      text not null default 'current',
  parent_id   uuid,
  tags        text[] not null default '{}'
);

create index ix_elements_project_branch on modelling.elements (project_id, branch);
create index ix_elements_project_branch_parent on modelling.elements (project_id, branch, parent_id);

create table modelling.relationships (
  id          uuid primary key,
  project_id  uuid not null,
  branch      uuid not null,
  from_id     uuid not null,
  to_id       uuid not null,
  label       text,
  technology  text,
  message_id  uuid              -- unused until Phase 4; nullable placeholder
);

create index ix_relationships_project_branch on modelling.relationships (project_id, branch);
```

### 4.2 Views Schema

```sql
-- schema: views
create table views.views (
  id                   uuid primary key,
  project_id           uuid not null,
  name                 text not null,
  root_element_id      uuid,              -- null = top-level context view
  visible_element_ids  uuid[] not null default '{}',
  active_lens          text,
  active_flow_id       uuid,              -- unused until Flows implemented; nullable placeholder
  created_at           timestamptz not null default now(),
  updated_at           timestamptz not null default now()
);

create index ix_views_project on views.views (project_id);

create table views.layout_positions (
  id          uuid primary key,
  view_id     uuid not null references views.views(id) on delete cascade,
  element_id  uuid not null,
  x           double precision not null,
  y           double precision not null,
  unique (view_id, element_id)
);

create table views.layout_edges (
  id              uuid primary key,
  view_id         uuid not null references views.views(id) on delete cascade,
  relationship_id uuid not null,
  route_points    jsonb,
  unique (view_id, relationship_id)
);
```

### 4.3 Workspaces Schema

```sql
-- schema: workspaces
create table workspaces.workspaces (
  id          uuid primary key,
  name        text not null,
  created_by  uuid not null,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);

create table workspaces.projects (
  id            uuid primary key,
  workspace_id  uuid not null references workspaces.workspaces(id),
  name          text not null,
  description   text,
  stream_id     uuid not null,       -- main model stream (created with project)
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now()
);

create index ix_projects_workspace on workspaces.projects (workspace_id);

create table workspaces.memberships (
  id            uuid primary key,
  workspace_id  uuid not null references workspaces.workspaces(id),
  user_id       uuid not null,
  role          text not null,       -- owner|editor|viewer
  created_at    timestamptz not null default now(),
  unique (workspace_id, user_id)
);
```

---

## 5. Identity Module

ASP.NET Core Identity with cookie sessions on the `identity` Postgres schema.

- `ApplicationUser` extends `IdentityUser` with a `DisplayName` property.
- External OIDC providers: GitHub + Google, configured via env vars (`Authentication__GitHub__ClientId`, etc.).
- Fallback email/password registration for solo self-hosters.
- Cookie settings: httpOnly, Secure (in production), SameSite=Strict, configurable expiry (default 7 days).
- No custom user profile table in Phase 1.

---

## 6. API Endpoints

### 6.1 Auth (`/api/auth`)

```
POST   /api/auth/register              → email/password register
POST   /api/auth/login                 → email/password login
POST   /api/auth/logout                → clear session
GET    /api/auth/external/{provider}   → initiate OIDC flow
GET    /api/auth/external/callback     → OIDC callback
GET    /api/auth/me                    → current user info
```

No auth required on auth endpoints (except `/me` and `/logout`).

### 6.2 Workspaces (`/api/workspaces`)

```
POST   /api/workspaces                              → CreateWorkspace (owner auto-assigned)
GET    /api/workspaces                              → ListWorkspaces (user's memberships)
PATCH  /api/workspaces/{id}                         → UpdateWorkspace (owner only)
POST   /api/workspaces/{id}/members                 → InviteMember (owner only)
DELETE /api/workspaces/{id}/members/{userId}         → RemoveMember (owner only)
POST   /api/workspaces/{workspaceId}/projects       → CreateProject (editor+)
GET    /api/workspaces/{workspaceId}/projects        → ListProjects (viewer+)
PATCH  /api/projects/{projectId}                    → UpdateProject (editor+)
DELETE /api/projects/{projectId}                    → DeleteProject (owner only)
```

### 6.3 Modelling (`/api/projects/{projectId}`)

```
POST   /api/projects/{projectId}/elements            → AddElement (editor+)
GET    /api/projects/{projectId}/elements             → ListElements (?kind=&status=&parentId=&cursor=&limit=) (viewer+)
GET    /api/projects/{projectId}/elements/{id}        → GetElement (viewer+)
PATCH  /api/projects/{projectId}/elements/{id}        → UpdateElement (editor+)
DELETE /api/projects/{projectId}/elements/{id}        → RemoveElement (editor+)

POST   /api/projects/{projectId}/relationships        → AddRelationship (editor+)
GET    /api/projects/{projectId}/relationships         → ListRelationships (?fromId=&toId=&cursor=&limit=) (viewer+)
GET    /api/projects/{projectId}/relationships/{id}    → GetRelationship (viewer+)
PATCH  /api/projects/{projectId}/relationships/{id}    → UpdateRelationship (editor+)
DELETE /api/projects/{projectId}/relationships/{id}    → RemoveRelationship (editor+)
```

**PATCH mapping:** A single PATCH with partial fields maps to multiple fine-grained events. The command handler diffs incoming fields against current state and emits only events for fields that changed.

### 6.4 Views (`/api/projects/{projectId}/views`)

```
POST   /api/projects/{projectId}/views                → CreateView (editor+)
GET    /api/projects/{projectId}/views                 → ListViews (viewer+)
GET    /api/projects/{projectId}/views/{id}            → GetView — includes layout positions (viewer+)
PATCH  /api/projects/{projectId}/views/{id}            → UpdateView (editor+)
DELETE /api/projects/{projectId}/views/{id}            → DeleteView (editor+)
PUT    /api/projects/{projectId}/views/{id}/layout     → SaveLayout — batch {elementId, x, y}[] + edge routes (editor+)
```

### 6.5 Cross-cutting

- Client-supplied UUIDs on all creates (idempotent).
- Cursor-based pagination on list endpoints (default 50, max 200). Response: `{ items: T[], cursor: string | null }`.
- Consistent error envelope: `{ type, title, detail, errors: [{field, message}] }`.
- Optimistic concurrency: stream version on modelling commands (409 on mismatch), `updated_at` on views (409 on stale).
- OpenAPI document at `/openapi/v1.json` in development. Scalar explorer at `/scalar`.

---

## 7. Program.cs Wiring

```csharp
// Services
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddWorkspacesModule();
builder.Services.AddModellingModule();
builder.Services.AddViewsModule();

// Scrutor: scan + decorate event-sourced command handlers
// Only ICommandHandler<,> implementations get TransactionBehavior wrapping
// Views + Workspaces use plain handler classes (no ICommandHandler, no decoration)
builder.Services.Scan(s => s.FromAssemblyOf<Program>()
    .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)))
    .AsImplementedInterfaces()
    .WithScopedLifetime());
builder.Services.Decorate(typeof(ICommandHandler<,>), typeof(TransactionBehavior<,>));

// Endpoints
app.MapIdentityEndpoints();
app.MapWorkspaceEndpoints();
app.MapModellingEndpoints();
app.MapViewEndpoints();
```

---

## 8. Testing Strategy

Testcontainers-backed Postgres throughout. No mocks.

### 8.1 Aggregate Tests (unit, no DB)

- `ModelDeciderTests` — every Decide path:
  - Add element: valid, invalid parent kind, missing parent, duplicate id
  - Update element: rename, reparent with containment violation, change status
  - Remove element: cascade removes relationships, cascade removes children recursively
  - Add relationship: valid, missing from/to element, duplicate id
  - Update relationship: label change, technology change
  - Remove relationship: valid, nonexistent
- `ModelEvolveTests` — every event type folds correctly into state

### 8.2 Integration Tests (Testcontainers)

- Command → event store → inline projection → read model query roundtrip
- Optimistic concurrency: concurrent stream updates → `ConcurrencyException`
- Cascade-remove: remove element with children and relationships → all read model rows deleted

### 8.3 Endpoint Tests (Testcontainers + WebApplicationFactory)

- HTTP request/response: status codes, body shape, error format
- Auth enforcement: unauthenticated → 401, wrong role → 403
- Pagination: cursor-based, respects limits
- PATCH partial update: send `{name: "New"}` → only `ElementRenamed` emitted
- Idempotent create: same id + same fields → 200 (not 409)

### 8.4 Convention Tests

- Every `ICommandHandler` implementation is discoverable by Scrutor scan
- All module DbContexts use `snake_case` naming convention
- No raw SQL without parameterization

---

## 9. Dev Seeding & Migrations

### 9.1 Migrations

Each module DbContext has its own migrations directory. `justfile` commands:

```
migrate-es name:       dotnet ef migrations add {{name}} --project src/Vellum --context EventStoreDbContext ...
migrate-identity name: dotnet ef migrations add {{name}} --project src/Vellum --context IdentityDbContext ...
migrate-workspaces name: dotnet ef migrations add {{name}} --project src/Vellum --context WorkspacesDbContext ...
migrate-modelling name: dotnet ef migrations add {{name}} --project src/Vellum --context ModellingDbContext ...
migrate-views name:    dotnet ef migrations add {{name}} --project src/Vellum --context ViewsDbContext ...
```

In development, `Program.cs` applies all pending migrations on startup.

### 9.2 Dev Seed

A `just seed` command or startup code (gated on `IsDevelopment`) creates:
- A default user (email: `dev@vellum.local`, password: `Dev123!`)
- A default workspace ("Dev Workspace") + project ("Sample Architecture")
- Seed elements: 2 actors, 3 systems, a few apps/stores inside one system, components inside one app — with relationships
- Seed data goes through the real command pipeline so inline projections fire and read models are populated

### 9.3 API Explorer

Scalar API reference at `/scalar` in development for manual endpoint testing.

---

## 10. Decisions

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| Event granularity | Per-field (13 event types) | Clean diffs for Phase 3 drafts, descriptive audit trail |
| PATCH → events | Diff incoming partial fields, emit only changed | Ergonomic API (one call per save) with fine-grained events |
| Remove semantics | Cascade-remove children + relationships | Architecture diagrams aren't databases; dangling refs have no meaning |
| Endpoint grouping | One `*Endpoints.cs` per module with `MapGroup` | Follows anthology pattern; keeps related routes together |
| Command handler wiring | Scrutor assembly scan + decoration | Automatic discovery; no per-handler registration |
| View concurrency | `updated_at` optimistic concurrency | Simple, sufficient for mutable CRUD |
| Auth | Full ASP.NET Core Identity + OIDC in Phase 1 | Production-shaped API from day one |
| Read model strategy | Inline projections only | Canvas needs immediate consistency; nothing heavy enough for async |
