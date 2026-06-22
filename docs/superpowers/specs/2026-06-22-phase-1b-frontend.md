# Phase 1b — Frontend: React SPA, Canvas, App Shell & Design System

> Companion to `phase-1a-backend.md`, `PRD.md`, `SDD.md`, `UI_UX.md`. Builds against the Phase 1a backend API. Scope: everything the user sees and interacts with for Phase 1 (Modelling).

---

## 1. Scope

This spec covers the frontend SPA for Phase 1. The backend API (Phase 1a) is the sole data source.

**In scope:**
- SPA scaffolding (Vite 6, React 19, TanStack Router/Query, Zustand, OpenAPI codegen)
- Design system (shadcn/ui + Tailwind v4 with the technical-drafting palette)
- App shell (top bar, navigator panel, detail panel, command palette, lens bar)
- Canvas (React Flow v12 with custom C4-styled nodes, orthogonal edges)
- Semantic zoom (two-tier: full card / label chip by text legibility)
- Drill-in navigation with strata breadcrumb
- Element/relationship CRUD (draw-to-create, inline rename, detail panel editing)
- Status lens + metadata lens
- Views (create, switch, save layout, auto-layout assist via elkjs)
- Workspace/project selection
- Auth flow (login page, OIDC + email/password, session management)
- Empty states, optimistic updates, error handling (including 409 conflict)
- Keyboard shortcuts and command palette

**Out of scope (later phases):**
- Flows lens + flow stepper (Phase 2)
- Docs/MDX authoring (Phase 2)
- Drafts/review/merge + diff overlay (Phase 3)
- Message overlay lens (Phase 4)
- AI/MCP chat panel (Phase 4)
- Share links (Phase 2+)
- Export (Phase 2+)
- Comments (Phase 3)
- E2E tests (deferred until more surface area)

---

## 2. Technology Stack

| Concern | Choice | Version |
|---------|--------|---------|
| Framework | React | 19.x |
| Build | Vite | 6.x |
| Language | TypeScript (strict mode) | 5.x |
| Routing | TanStack Router (file-based) | 1.x |
| Server state | TanStack Query | 5.x |
| Client state | Zustand | 5.x |
| Canvas | React Flow | 12.x |
| Component library | shadcn/ui (CLI v2, Tailwind v4 native) | latest |
| Styling | Tailwind CSS | 4.x |
| API client codegen | @hey-api/openapi-ts | latest |
| Auto-layout | elkjs | latest |
| Command palette | cmdk | latest |
| Test runner | Vitest | latest |
| Component testing | @testing-library/react | latest |
| Display font | Space Grotesk | — |
| Body font | Inter | — |
| Mono font | JetBrains Mono | — |

All dependencies are MIT or Apache-2.0 licensed.

---

## 3. Project Structure

### 3.1 Location

`src/Vellum.Web/` — a standalone frontend directory, no .csproj. Decoupled from the .NET build. Vite builds to `../Vellum/wwwroot`. The .NET app serves the built output via `UseStaticFiles()` + `MapFallbackToFile("index.html")`.

### 3.2 Dev Proxy

`vite.config.ts` proxies `/api` to `http://localhost:5000` (the .NET backend). Two terminals in dev: `dotnet run --project src/Vellum` + `cd src/Vellum.Web && npm run dev`.

### 3.3 OpenAPI Codegen Workflow

- `Microsoft.Extensions.ApiDescription.Server` on the backend generates `openapi.json` at build time (no running server, no DB needed)
- `openapi.json` is committed to the repo (the API contract, reviewable in PRs)
- The generated TypeScript client (`src/Vellum.Web/src/api/generated/`) is gitignored
- `just openapi` = `dotnet build src/Vellum` → copy JSON → `cd src/Vellum.Web && npm run generate`

### 3.4 File Tree

```
src/Vellum.Web/
  package.json
  vite.config.ts
  tsconfig.json
  tailwind.css                      — Tailwind v4 theme tokens + imports
  components.json                   — shadcn/ui config
  openapi-ts.config.ts              — hey-api codegen config
  src/
    main.tsx                        — app entry, mounts <App />
    app.tsx                         — providers: QueryClientProvider, RouterProvider
    routes/                         — TanStack Router file-based routes
      __root.tsx                    — root layout (global providers, error boundary)
      _auth.tsx                     — unauthenticated layout (login/register pages)
      _auth/
        login.tsx                   — login page
        register.tsx                — register page
      _app.tsx                      — authenticated app shell layout
      _app/
        index.tsx                   — workspace/project picker
        projects/
          $projectId.tsx            — main canvas workspace (shell + canvas)
    components/
      ui/                           — shadcn primitives (Button, Sheet, Dialog, Input, etc.)
      shell/
        top-bar.tsx                 — workspace/project selector + breadcrumb + ⌘K trigger
        navigator.tsx               — model tree + views list
        detail-panel.tsx            — element/relationship editor (Sheet)
        lens-bar.tsx                — lens toggles + zoom + tidy button
        strata-breadcrumb.tsx       — drill path with C4 level labels
      canvas/
        canvas-view.tsx             — React Flow wrapper + viewport management
        nodes/
          c4-element-node.tsx       — full card node (kind-spine, tech badges, status)
          c4-label-chip-node.tsx    — collapsed LoD node (name + icon + status dot)
          node-types.ts             — React Flow nodeTypes registry
        edges/
          c4-edge.tsx               — orthogonal edge with label chip
          edge-types.ts             — React Flow edgeTypes registry
        create-popover.tsx          — kind picker on draw-to-create
      command-palette/
        command-palette.tsx         — cmdk dialog with actions
    stores/
      canvas-store.ts              — Zustand: drill path, root, lens, zoom
      shell-store.ts               — Zustand: panel state, selection, active view
    api/
      client.ts                    — configured hey-api client instance
      generated/                   — gitignored: @hey-api/openapi-ts output
    hooks/
      use-elements.ts              — TanStack Query: list, get, add, update, remove elements
      use-relationships.ts         — TanStack Query: list, get, add, update, remove relationships
      use-views.ts                 — TanStack Query: list, get, create, update, delete views, save layout
      use-auth.ts                  — TanStack Query: me, login, register, logout
      use-lod.ts                   — level-of-detail threshold hook (zoom → node tier)
    lib/
      layout.ts                    — elkjs auto-layout wrapper
      kind-colors.ts               — kind → color mapping for node spines
      strata.ts                    — element kind → C4 stratum label mapping
```

---

## 4. Design System

### 4.1 Palette

Mapped from UI/UX doc §2 to Tailwind v4 CSS custom properties and shadcn theme variables.

**Dark theme (default):**

| Token | Hex | Usage |
|-------|-----|-------|
| `--background` | `#14181D` (Ink) | Canvas, page background |
| `--foreground` | `#F6F7F7` (Paper) | Primary text |
| `--card` | `#1E242B` (Graphite) | Panels, nodes, cards |
| `--card-foreground` | `#F6F7F7` | Text on cards |
| `--primary` | `#0E8EA8` (Plotter) | Brand accent, active states, primary buttons |
| `--primary-foreground` | `#FFFFFF` | Text on primary |
| `--muted` | `#2A3140` | Muted backgrounds, disabled states |
| `--muted-foreground` | `#8899AA` | Muted text, descriptions |
| `--border` | `#2E3744` | Borders, dividers |

**Light theme:**

| Token | Hex | Usage |
|-------|-----|-------|
| `--background` | `#F6F7F7` (Paper) | Page background |
| `--foreground` | `#1E242B` (Graphite) | Primary text |
| `--card` | `#FFFFFF` | Panels, nodes |
| `--primary` | `#0E8EA8` (Plotter) | Same accent |

**Semantic tokens (both themes):**

| Token | Hex | Usage |
|-------|-----|-------|
| `--status-current` | transparent | No visual treatment |
| `--status-planned` | `#3B7DD8` | Planned elements (dashed border) |
| `--status-deprecated` | `#B7791F` | Deprecated elements (amber tint) |
| `--status-removed` | `#B5483D` | Removed elements (only in drafts, Phase 3) |
| `--kind-actor` | `#7C6F9B` | Left spine color for actors (muted purple) |
| `--kind-system` | `#5B8A72` | Left spine color for systems (muted green) |
| `--kind-app` | `#4A7FB5` | Left spine color for apps (muted blue) |
| `--kind-store` | `#8B7355` | Left spine color for stores (muted brown) |
| `--kind-component` | `#6B8E9B` | Left spine color for components (muted teal) |

Kind colors are muted earth/drafting tones to match the technical-drafting aesthetic. They must be distinct from status colors (blue/amber/red) and meet WCAG AA contrast on both themes. Exact values may be adjusted during implementation.

### 4.2 Typography

| Role | Font | Weight | Usage |
|------|------|--------|-------|
| Display | Space Grotesk | 500–700 | Stratum labels, headings, node names |
| Body | Inter | 400–500 | Panel text, descriptions, UI labels |
| Mono | JetBrains Mono | 400 | Tech badges, kind badges, IDs, code |

All open-licensed. Loaded via `@fontsource` packages (self-hosted, no external CDN).

### 4.3 Component Principles

- **shadcn/ui** for all chrome: top bar, navigator, detail panel, lens bar, dialogs, command palette, forms, toasts.
- **Bespoke Tailwind-styled components** for canvas nodes and edges — no shadcn primitives inside React Flow nodes (render perf).
- **Dark theme is the default.** Light theme has full parity via CSS custom properties. Toggle in account settings or top bar.

---

## 5. App Shell

### 5.1 Layout

Three-column layout with top bar and bottom lens bar, matching UI/UX doc §3.

```
┌─────────────────────────────────────────────────────────┐
│ TopBar                                                   │
├──────────┬──────────────────────────────────┬───────────┤
│Navigator │         Canvas (React Flow)       │  Detail   │
│ (240px,  │                                   │  Panel    │
│ resizable│                                   │ (320px,   │
│ collaps.)│                                   │ on-demand)│
├──────────┴──────────────────────────────────┴───────────┤
│ LensBar                                                  │
└─────────────────────────────────────────────────────────┘
```

- Navigator: 240px default, resizable, collapsible to icon-only rail.
- Detail panel: 320px, opens on selection (shadcn Sheet, right side). Closes on deselect.
- Canvas fills remaining space.

### 5.2 Top Bar

Left to right:
1. **Workspace selector** — dropdown showing current workspace, switch workspace
2. **Project selector** — dropdown showing projects in current workspace, switch project
3. **Strata breadcrumb** — drill path: `Context` → `System: Orders` → `Container: Orders API`. Each segment clickable to ascend. Shows C4 level label at each step.
4. **Spacer**
5. **Search trigger** — opens command palette focused on search
6. **⌘K button** — opens command palette

### 5.3 Navigator

Two sections in a vertical split:

**Model tree:** Elements displayed in containment hierarchy (systems at top, apps nested under their system, components nested under their app). Actors shown as a separate top-level group. Each tree node shows: kind icon + name + status dot. Click to select (opens detail panel, highlights on canvas). Double-click to drill in.

**Views list:** Saved views for the current project. Click to switch view. "+" button to create view from current canvas state. Active view highlighted.

### 5.4 Detail Panel

Opens as a shadcn Sheet on element/relationship selection.

**Element detail:**
- Name (inline editable, Space Grotesk display)
- Kind badge (read-only, mono — kind is immutable)
- Description (textarea)
- Technology (text input)
- Owner (dropdown, populated from workspace members)
- Status (select: current / planned / deprecated)
- Tags (tag input with autocomplete, inline-create)
- Parent (read-only, shows containment path)

**Relationship detail:**
- Label (text input)
- Technology (text input)
- From → To (read-only, element names)

All edits dispatch PATCH mutations via TanStack Query. Optimistic updates with rollback on error.

### 5.5 Lens Bar

Bottom bar with:
- **Status lens toggle** — when active, status colors/styles applied to nodes (dashed borders for planned, amber tint for deprecated)
- **Metadata lens toggle** — when active, tech badges and owner avatars visible on nodes (hidden by default to keep the canvas clean; metadata lens is the "dense" view)
- **Zoom controls** — slider + zoom-to-fit button
- **Tidy button** — runs elkjs auto-layout on the current view
- **View selector** — dropdown to switch between saved views

### 5.6 Command Palette

cmdk dialog, opened via `⌘K` or clicking the trigger in the top bar.

**Actions:**
- Jump to element (fuzzy search by name)
- Switch view
- Add element (opens kind picker → places on canvas)
- Toggle status lens / metadata lens
- Tidy layout
- Zoom to fit

Each action has a discoverable keyboard shortcut shown in the palette.

---

## 6. Canvas

### 6.1 React Flow Configuration

- Custom node types: `c4-element` (full card), `c4-label-chip` (collapsed)
- Custom edge type: `c4-edge` (orthogonal with label chip)
- `fitView` on initial load
- `panOnDrag`, `zoomOnScroll`, `selectionOnDrag` (box select)
- Minimap enabled (React Flow built-in)
- Snap-to-grid: 16px grid

### 6.2 Full Card Node

The signature C4 element card (UI/UX doc §6):

```
┌─┰───────────────────────────────┐
│ ┃  Orders API            [APP]   │   kind-spine (4px left border, color by kind)
│ ┃  ───────────────────────────  │   name (Space Grotesk 500), kind badge (mono)
│ ┃  Handles checkout & fulfilment │   description (Inter 400, muted, 1–2 lines, truncated)
│ ┃  `dotnet` · `postgres`   ◎ NB  │   tech badges (mono) · owner avatar
└─┸───────────────────────────────┘
```

- **Kind-spine:** 4px colored left border. Color per kind (see §4.1 kind tokens).
- **Status encoding:** `current` = solid border; `planned` = dashed border; `deprecated` = amber tint on card background. Applied when status lens is active.
- **Stores:** subtle cylinder shape (rounded bottom corners).
- **Actors:** rounded top with person silhouette affordance.
- **Tech badges** and **owner avatar** only visible when metadata lens is active.
- Node width: 280px fixed. Height: auto based on content.

### 6.3 Label Chip Node

Collapsed LoD tier (UI/UX doc §7):

```
┌──────────────────┐
│ 🔷 Orders API     │   kind icon + name + status color
└──────────────────┘
```

- Single line: kind icon (small, colored) + name (Inter 400) + status dot
- Fixed height (~32px), width auto by name length
- No description, no tech, no owner

### 6.4 Level-of-Detail Switching

Two-tier semantic zoom, triggered by rendered text legibility:

- Compute `effectiveFontSize = baseFontSize * currentZoom`
- If `effectiveFontSize < 10px` → render as label chip
- If `effectiveFontSize >= 10px` → render as full card
- Threshold is a constant in `lib/lod.ts`, tunable
- Transition: CSS `opacity` + `scale` with `transition: 150ms ease`. `prefers-reduced-motion` → instant swap (no transition)

Implemented as a `useLod()` hook that reads React Flow's zoom from `useViewport()` and returns the active tier.

### 6.5 Edges

- Base: React Flow `smoothstep` edge type, customized for orthogonal routing with rounded corners
- **Label chip:** small pill mid-edge showing relationship label (Inter 400) + technology (mono, muted). Only visible above a zoom threshold.
- **Arrowhead:** on the target end. `markerEnd` on the React Flow edge.
- When metadata lens is active, technology text is emphasized.

### 6.6 Creating Elements (Draw-to-Create)

IcePanel-style interaction (UI/UX doc §9):

1. User drags from a node's connection handle to empty canvas space
2. On drop: a small popover appears at the drop position with a kind picker (list of valid child kinds based on containment rules — e.g., from a System, only App and Store are offered)
3. User picks a kind → new element created (POST), relationship created (POST), element placed at drop position
4. Element name drops into inline edit mode (click-away or Enter to confirm)

**Secondary creation paths:**
- Command palette: "Add element..." → kind picker → placed at canvas center
- Navigator: context menu or "+" button

### 6.7 Element Interaction

| Action | Trigger | Result |
|--------|---------|--------|
| Select | Click node | Detail panel opens, node highlighted |
| Multi-select | Shift+click or box-drag | Multiple nodes selected |
| Move | Drag node | Position updates, layout saves (debounced 300ms) |
| Drill in | Double-click node with children | Canvas transitions to children view |
| Drill out | Click breadcrumb segment | Canvas transitions back up |
| Inline rename | Double-click node label | Text input overlay on name |
| Delete | Backspace/Delete with selection | Confirmation dialog, then DELETE (cascade) |
| Connect | Drag from handle to existing node | Create relationship |

---

## 7. Drill-In Navigation

### 7.1 Drill Mechanics

Each C4 level is its own canvas view. The model is flat (all elements loaded), but the canvas filters to show only elements at the current drill level:

- **Top level (Context):** shows actors + systems + their relationships
- **Drill into a System:** shows that system's apps + stores + their relationships, plus external relationships to elements outside this system
- **Drill into an App:** shows that app's components + their relationships

The `currentRootId` in `canvas-store` determines what's shown. `null` = top level.

### 7.2 Strata Breadcrumb

The breadcrumb is the signature wayfinding element (UI/UX doc §4):

```
Context  ›  System: Orders  ›  Container: Orders API
```

- Each segment: C4 stratum label (Context / System / Container / Component) + element name
- Top level is always "Context"
- Click any segment to ascend to that level
- Styled in display font (Space Grotesk), stratum label in muted mono

### 7.3 Drill Transition

- Brief zoom-through animation: canvas scales toward the double-clicked element, fades, then fades in the new level centered
- Duration: 300ms ease-in-out
- `prefers-reduced-motion` → instant swap (no animation)

### 7.4 Auto-Derived View Contents

When drilling into an element, the canvas auto-derives its contents:
- Children of the root element (direct children only)
- Relationships where both from and to are visible children
- Relationships where one end is a visible child and the other is outside the current scope — the outside element is rendered as a **stub node**: a label chip showing the external element's name and kind icon, positioned at the edge of the canvas. Clicking a stub node navigates to that element's parent context (drills out and selects it). Stub nodes are not movable or editable — they exist only to show cross-boundary relationships.

---

## 8. Views & Layout

### 8.1 Views

A view is a persisted canvas state:
- `name` — user-assigned
- `rootElementId` — drill context (null = top-level)
- `visibleElementIds` — which elements are shown (auto-derived by default, customizable)
- `activeLens` — which lens was active
- Layout positions (separate table, per element per view)
- Layout edges (separate table, per relationship per view, with route points)

**Create view:** command palette or navigator "+" button. Captures current root, visible elements, lens, and layout.

**Switch view:** click in navigator. Loads the view's layout, sets drill context, applies lens.

**Default view:** on first project load with no saved views, show top-level context (all actors + systems). Prompt to save as a view.

### 8.2 Layout Persistence

- **Manual placement is primary.** Positions persist per view.
- On node drag-end: debounced `PUT /api/projects/{projectId}/views/{viewId}/layout` (300ms debounce). Sends all positions + edge routes for the current view.
- Layout is fire-and-forget — no optimistic concurrency on layout saves (low-stakes, last-write-wins).

### 8.3 Auto-Layout (Tidy)

- **Explicit action only** — "Tidy" button in lens bar or command palette
- Runs elkjs with layered/orthogonal algorithm over the current view's visible elements
- After elkjs produces positions, applies them to the canvas and triggers a layout save
- Use cases: first-draft placement for a new view, cleaning up after adding several elements, starting fresh

---

## 9. API Integration

### 9.1 Data Loading

On project route load (`$projectId.tsx`):
1. `useElements(projectId)` — `GET /api/projects/{projectId}/elements` (all elements, paginated with auto-fetching all pages if needed)
2. `useRelationships(projectId)` — `GET /api/projects/{projectId}/relationships` (same)
3. `useViews(projectId)` — `GET /api/projects/{projectId}/views` (list only)
4. On view switch: `useView(projectId, viewId)` — `GET /api/projects/{projectId}/views/{viewId}` (includes layout)

TanStack Query config:
- `staleTime: 30_000` (30s) for model data
- `staleTime: 60_000` (1min) for view list
- `retry: 3` with exponential backoff

### 9.2 Mutations

All mutations via TanStack Query `useMutation` with optimistic updates:

| Action | Endpoint | Optimistic behavior |
|--------|----------|-------------------|
| Add element | `POST /elements` | Insert into query cache immediately |
| Update element | `PATCH /elements/{id}` | Update in cache, roll back on error |
| Remove element | `DELETE /elements/{id}` | Remove from cache (+ cascaded relationships) |
| Add relationship | `POST /relationships` | Insert into cache |
| Update relationship | `PATCH /relationships/{id}` | Update in cache |
| Remove relationship | `DELETE /relationships/{id}` | Remove from cache |
| Create view | `POST /views` | Insert into view list cache |
| Update view | `PATCH /views/{id}` | Update in cache |
| Delete view | `DELETE /views/{id}` | Remove from cache |
| Save layout | `PUT /views/{id}/layout` | No optimistic needed (positions already local) |

### 9.3 Auth

- **Login page** (`_auth/login.tsx`): full-page centered card (shadcn Card). External OIDC buttons (GitHub, Google — rendered if configured) + email/password form below.
- **Register page** (`_auth/register.tsx`): similar card with email + password + display name.
- **Session:** httpOnly cookie set by the backend. Frontend never touches tokens.
- **Auth check:** `useAuth()` hook calls `GET /api/auth/me`. Returns user info or redirects to login on 401.
- **Route protection:** `_app.tsx` layout's `beforeLoad` calls auth check. Unauthenticated → redirect to `/login`.
- **Logout:** `POST /api/auth/logout` → clear query cache → redirect to `/login`.

### 9.4 Error Handling

| Error | UI Treatment |
|-------|-------------|
| Validation (400) | Inline field errors in detail panel, from `errors[].field` + `errors[].message` |
| Unauthorized (401) | Redirect to login |
| Forbidden (403) | Toast: "You don't have permission for this action" |
| Conflict (409) | Toast: "This changed in another session. Reload to see the latest." + Reload button |
| Not Found (404) | Toast: "Element not found — it may have been deleted" |
| Network error | Toast: "Connection lost. Retrying..." (TanStack Query auto-retries) |
| Server error (500) | Toast: "Something went wrong. Try again." |

### 9.5 Empty States

Following UI/UX doc §18:
- **Empty project (no elements):** centered message: "Start with a system. Draw a connection to add the next one." with a prominent "Add System" button.
- **Empty drill (no children):** "Nothing inside *Orders API* yet. Add an app or component to get started."
- **Empty views list:** "No saved views yet. Arrange the canvas and save a view."
- **Loading:** skeleton placeholders in navigator and detail panel. Canvas: centered spinner on initial load. Subsequent mutations are optimistic (instant).

---

## 10. State Management

### 10.1 Canvas Store (Zustand)

```typescript
interface CanvasState {
  drillPath: { elementId: string; name: string; kind: string }[];
  currentRootId: string | null;
  activeLens: 'none' | 'status' | 'metadata';
  zoomLevel: number;

  drillInto: (element: { id: string; name: string; kind: string }) => void;
  drillTo: (index: number) => void;  // breadcrumb click
  setLens: (lens: CanvasState['activeLens']) => void;
  setZoom: (zoom: number) => void;
  reset: () => void;
}
```

### 10.2 Shell Store (Zustand)

```typescript
interface ShellState {
  navigatorOpen: boolean;
  detailPanelOpen: boolean;
  selectedElementId: string | null;
  selectedRelationshipId: string | null;
  activeViewId: string | null;
  commandPaletteOpen: boolean;

  toggleNavigator: () => void;
  selectElement: (id: string | null) => void;
  selectRelationship: (id: string | null) => void;
  setActiveView: (id: string | null) => void;
  toggleCommandPalette: () => void;
}
```

---

## 11. Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `⌘K` / `Ctrl+K` | Open command palette |
| `Escape` | Close panel / deselect / close palette |
| `Backspace` / `Delete` | Delete selected element(s) (with confirmation) |
| `⌘Z` / `Ctrl+Z` | Undo (optimistic mutation rollback, single level) |
| `⌘+` / `⌘-` | Zoom in / out |
| `⌘0` | Zoom to fit |
| `Enter` | Drill into selected element |
| `Backspace` (no selection) | Drill out (ascend one level) |
| `1` | Toggle status lens |
| `2` | Toggle metadata lens |
| `T` | Tidy layout |
| `N` | Add new element (opens kind picker) |

Shortcuts are discoverable in the command palette.

---

## 12. Testing Strategy

### 12.1 Framework

Vitest + @testing-library/react. No E2E in Phase 1b — manual testing against the real backend. E2E (Playwright) deferred.

### 12.2 Test Focus

**Zustand stores:**
- Canvas store: drill in/out updates path correctly, lens toggling, reset
- Shell store: selection, panel open/close, view switching

**Custom nodes:**
- Full card renders with correct kind-spine color, name, description, tech badges
- Label chip renders with kind icon + name
- LoD hook returns correct tier based on zoom level

**TanStack Query hooks:**
- Mock API responses via MSW or manual query client seeding
- Optimistic update: cache updates immediately on mutation
- Rollback: cache reverts on mutation error

**Command palette:**
- Renders actions, fuzzy search filters correctly, action dispatch

**Auth guard:**
- Redirects to login on 401
- Renders app shell when authenticated

### 12.3 What Not to Test

- shadcn/ui primitives (tested upstream)
- React Flow internals (tested upstream)
- Pixel-perfect layout (manual visual QA)

---

## 13. Decisions

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| SPA location | `src/Vellum.Web/` | Decoupled from .NET build, clean separation, builds to `../Vellum/wwwroot` |
| Dev proxy | Vite proxy `/api` → localhost:5000 | 3 lines of config, no ASP.NET SPA middleware |
| Router | TanStack Router (file-based) | Type-safe, matches anthology, loader pattern pairs with TanStack Query |
| State management | Zustand | Simple store model, canvas-friendly, no provider overhead, React Flow ecosystem alignment |
| API client | @hey-api/openapi-ts | Type-safe generated client, spec-driven |
| OpenAPI spec | Committed to repo, build-time generated | Reviewable API contract, no running server needed for codegen |
| Generated client | Gitignored, regenerated via justfile | Deterministic from spec, avoids noisy diffs |
| Component library | shadcn/ui v2 + Tailwind v4 | Copy-owned primitives, full theme control, Tailwind v4 native |
| Canvas nodes | Bespoke Tailwind-styled, not shadcn | Render perf, custom C4 visual language |
| LoD tiers | Two-tier (full card ↔ label chip) | UI/UX doc §7, simple, text-legibility-driven |
| LoD threshold | effectiveFontSize < 10px → chip | Tunable constant, legibility-based |
| Auto-layout | elkjs, explicit "Tidy" action only | Manual placement primary, auto-layout is assist (UI/UX doc §5) |
| Typography | Space Grotesk / Inter / JetBrains Mono | All open-licensed, match UI/UX doc |
| Font loading | @fontsource (self-hosted) | No external CDN, works offline/self-hosted |
| Kind colors | Tuned at implementation time | Not specified in UI/UX doc, need to ensure contrast + distinction |
| Test framework | Vitest + Testing Library | Fast, modern, good DX |
| E2E tests | Deferred | Not enough surface area yet; manual QA sufficient for Phase 1b |
| Build order | Shell-first | Visual feedback early, canvas slots into frame |
