# Vellum — UI/UX Design Document

> Status: **Draft v0.1**. Companions: `PRD.md`, `SDD.md`. Desktop-first. Component layer: **shadcn/ui + Tailwind v4**. Canvas: **React Flow** with bespoke nodes.

---

## 1. Principles

- **Dense, technical-first by default.** The default view is detail-rich for engineers (tech, status, owner visible). The clean stakeholder view is a toggleable **Presentation lens**, not the default — so C4's multi-audience value survives without making engineers hunt.
- **The model shows through the UI.** Edit once, see it everywhere; views are projections, not copies. The chrome should make "this is one model, many views" legible.
- **Spend boldness in one place.** The canvas — its node language and drill navigation — is the signature. Everything around it (panels, toolbars, dialogs) stays quiet and precise.
- **Functional colour is data.** Status and diff colours carry meaning, so the base palette is deliberately neutral; colour earns its place only when it encodes something true (a status, a change, a technology).

---

## 2. Design language

The cliché to avoid for a tool like this is the near-black dashboard with a single acid accent. Instead the identity is grounded in the subject's real world — **technical drafting and cartography** (C4 is "a map of your system, zoom in for detail"). The base is the quiet of drafting paper and plotter ink; meaning is carried by precise marks, not decoration.

**Palette** (maps to shadcn/Tailwind v4 CSS variables; dark is the default theme)

- `Ink` `#14181D` — dark-mode canvas/background (a deep blue-slate, never pure black)
- `Graphite` `#1E242B` — dark surfaces/panels; on light, the primary text colour
- `Paper` `#F6F7F7` — light-mode background (cool, not cream)
- `Plotter` `#0E8EA8` — brand/accent (a drafting cyan, used sparingly: primary actions, active stratum, brand)
- Neutrals: a 10-step graphite ramp for borders, muted text, dividers

**Functional colours** (semantic tokens, muted to sit on the quiet base — not neon)

- Status: `current` (neutral, no colour) · `planned` `#3B7DD8` · `deprecated` `#B7791F` · `removed` `#B5483D`
- Diff: `added` `#2F855A` · `modified` `#B7791F` · `removed` `#B5483D`

**Typography** — spend the character budget on display + mono; keep body neutral for dense legibility.

- *Display* (brand, stratum labels, headings): a technical grotesque with character (e.g. Space Grotesk or a licensed grotesk) — used with restraint.
- *Body/UI* (dense panel and label text): a highly legible neutral sans (Inter/Geist). Restraint here is deliberate — legibility wins in dense UI.
- *Data/mono* (tech badges, element kinds, IDs, schema, code): a precise mono (e.g. IBM Plex Mono / JetBrains Mono). Mono on technical marks is both functional and a recurring texture of the identity.

**Signature:** *strata wayfinding* — the drill path rendered as a depth-aware breadcrumb (a cartographic sense of which C4 stratum you're in and how you got there), paired with the C4 node card (kind-spine + mono tech badge + status edge). That pairing is the one thing the product is remembered by.

These map onto Tailwind v4's CSS-first tokens and shadcn's theme variables (`--background`, `--foreground`, `--primary`, plus added `--status-*` / `--diff-*`). Dark default, light parity.

---

## 3. App shell

Desktop-first, three columns + bars; all chrome is shadcn/Tailwind.

```
┌───────────────────────────────────────────────────────────┐
│ Top bar: project · branch/draft pill · search · share · ⌘K │
├──────────┬─────────────────────────────────────┬───────────┤
│ Navigator│  Breadcrumb (drill path / strata)    │  Detail    │
│ (model   │                                      │  panel     │
│  tree +  │            CANVAS                     │  (Sheet:   │
│  views)  │         (React Flow)                  │  element/  │
│          │                                      │  relation) │
├──────────┴─────────────────────────────────────┴───────────┤
│ Lens bar · Flow stepper · zoom · diff legend (in drafts)    │
└───────────────────────────────────────────────────────────┘
```

- **Navigator** (left): the model tree (containment hierarchy) + saved views. shadcn tree/accordion.
- **Detail panel** (right): a shadcn `Sheet`/side panel for the selected element or relationship — name, description, technology, owner, tags, status, attached docs/schemas. Edits dispatch commands.
- **Lens bar + flow stepper** (bottom): toggle lenses and step through flows.
- **Command palette** (`⌘K`, cmdk): jump to any element/view/doc, run actions — the keyboard-first spine for the technical audience.

---

## 4. Canvas — navigation (drill-in)

Each C4 level is its own view. Double-click a system/app to **descend** into its internals (a view rooted at that element); the **breadcrumb** is the drill path back up. Descent/ascent uses a brief zoom-through transition so drill-in *feels* continuous (respecting `prefers-reduced-motion`: instant swap when set). The breadcrumb doubles as the strata wayfinding signature — it shows the level kind (Context / Container / Component) at each step.

A view defaults to auto-deriving its contents (the root's children + their relationships) but is saveable/customisable.

---

## 5. Canvas — layout

**Manual placement is primary.** You arrange elements; the model syncs. Positions persist per view (the layout table — kept out of the event log). **Auto-layout is an assist**, not the default: an explicit "Tidy" action runs elk (layered/orthogonal) over the current view — for first-draft placement, for newly added nodes, or to clean up a mess — after which you adjust by hand. This is deliberate: hand-placed diagrams are a large part of why IcePanel reads well and auto-generated C4 reads poorly.

---

## 6. Canvas — node design

The node is the product's signature object. Two LoD tiers, switched by **legibility** (see §7).

**Full card** (default / when text is legible):

```
┌─┰───────────────────────────────┐
│ ┃  Orders API            [APP]   │   kind-spine (left edge, colour by kind)
│ ┃  ───────────────────────────  │   name (display), kind badge
│ ┃  Handles checkout & fulfilment │   description (muted, 1–2 lines)
│ ┃  `dotnet` · `postgres`   ◎ NB  │   tech (mono badges) · owner avatar
└─┸───────────────────────────────┘   status encoded on the spine/border
```

- **Kind-spine**: a coloured left edge differentiates actor / system / app / store / component at a glance (shape alone is too subtle; the spine is the quick read).
- **Tech** as mono badges (identity texture + scannable).
- **Status**: `current` neutral; `planned` dashed border; `deprecated` amber tint; `removed` shown only in drafts (see §10).
- Stores get a cylinder silhouette; actors a rounded/person affordance; otherwise the card is consistent so the model reads as one language.

**Label chip** (collapsed, when zoomed far enough that card text would be unreadable): name + kind icon + status colour only. Keeps overviews legible instead of a wall of unreadable cards.

---

## 7. Canvas — level-of-detail (intra-level)

A disciplined **two-tier semantic zoom**, triggered by *rendered text legibility*, not arbitrary breakpoints: above a px-size threshold the node renders as a full card; below it, it collapses to a label chip. This gives the dense default you want whenever detail is readable, and a clean map when you pull back to orient before drilling. (Reveal animation is a fade/scale; reduced-motion → instant.)

---

## 8. Canvas — edges

- **Orthogonal routing** with rounded corners (reads as schematic, not organic), labels on a small chip mid-edge, technology in mono.
- Direction by arrowhead; bidirectional supported.
- In the **message lens** (§10), an edge can expand to `producer → [Message] → consumer`, and a message with multiple consumers renders as a hub.

---

## 9. Creating & editing elements

**Primary: draw a connection into empty space** (IcePanel-style). Drag from an element's edge to blank canvas → drop → a new element is created, connected, and drops into inline-rename. This keeps modelling in the flow of drawing and adds the object to the model for reuse. Pick the new element's kind from a small inline popover (mono kind list).

Secondary paths: a small **palette** (drag a kind onto the canvas) and the **command palette** ("Add app…"). Editing details happens in the right **Sheet**; inline rename on double-click of the label.

---

## 10. Lenses & drafts

**Lenses** are client-side filters over one loaded model, toggled in the lens bar: Metadata, Status, Flows, Message overlay, and **Presentation** (strips tech/owner/IDs to the clean stakeholder read — the inverse of the dense default).

**Message overlay lens:** when active, relationships that carry a `message_id` expand visually — the direct edge splits into `producer → [Message node] → consumer(s)`. The **Message node** is a horizontal **pill/lozenge** — compact, visually distinct from the rectangular element cards, reading as "a thing passing through" rather than "a thing that exists." It shows: message name and schema format badge (e.g. `JSON Schema`). Clicking a message node opens its detail in the Sheet (schema content rendered as a read-only syntax-highlighted code block with format badge and copy-to-clipboard; producer and consumer lists). Messages with multiple consumers render as a hub (one inbound edge, multiple outbound). When the lens is toggled off, the expanded edges collapse back to direct relationships.

### 10.1 Flow stepper

When the **Flows** lens is active and a flow is selected, the bottom bar shows a **step-through control**: prev/next buttons, a step counter ("3 / 7"), and the current step's label. Each step highlights the referenced element or relationship on the canvas (dimming everything else); advancing animates the highlight to the next step. Flows are authored from the detail Sheet of a view — an ordered list of element/relationship references, each with an optional label. Add a step by clicking an element on the canvas while in "add step" mode; reorder via drag in the list.

### 10.2 Drafts

**Drafts** put the canvas in a visibly different mode (branch pill in the top bar, subtle canvas chrome shift). Changes render as a **live diff overlay**:

- *Added* → green ring.
- *Modified* → amber ring; changed fields flagged in the detail Sheet.
- *Removed* → ghosted + struck with a red outline (so reviewers can see what the draft deletes, rather than it silently vanishing).
- A **diff legend** sits in the bottom bar; **hold to peek original** temporarily shows the base state.

The **review panel** (shadcn side panel) lists the structural changeset; clicking an entry focuses/centres it on the canvas. **Conflicts** (from the SDD's three-way merge) appear as a resolvable list — each conflict shows base / ours / theirs with a pick-a-side (or edit) control; merge is blocked until all are resolved. **Decision records** are composed from the draft and auto-link the touched elements.

### 10.3 Comments & feedback

Comments attach to **elements**, **relationships**, or **draft changesets**. On the canvas, a comment indicator (small dot) appears on the node's corner; clicking opens a comment thread in the detail Sheet. In the review panel, inline comments appear next to their changeset entry. Unresolved comment count is visible in the draft pill (top bar). Comments are threaded (reply) but flat (no nested threads beyond one level).

---

## 11. Docs

Docs live in two places: **attached to an element** (opens in the detail Sheet or expands to a full editor) and as **standalone pages** organised in spaces (Navigator). Authoring is MDX in a **full CodeMirror editor** with MDX syntax highlighting — no split-pane preview in v1. `<LiveView/>` and other whitelisted components are typed as JSX directly; the rendered doc is viewed by navigating to the doc's read view (not a live preview pane beside the editor). Docs and the model cross-link both ways (an element lists its docs; a doc's element mentions deep-link to the canvas). MDX components are whitelisted/sandboxed (untrusted multi-tenant content).

---

## 11.1 Tags, owners & teams

**Tags:** inline-create in the detail Sheet — a tag input field where typing a new name creates the tag (with colour picker). Existing tags appear as autocomplete suggestions. Tags render as small coloured chips on element cards. Bulk-tagging via multi-select on the canvas + a "Tag" action.

**Owners/teams:** managed in **workspace settings** (team CRUD, member assignment) and **project settings** (team visibility). Assigned to elements inline via a dropdown in the detail Sheet. Owner avatars render on element cards.

**Management:** a "Tags" section in project settings lists all tags with rename, recolour, and delete (with usage count). Deleting a tag removes it from all elements.

---

## 12. Sharing & share links

A **Share** button in the top bar opens a popover for the current view or doc. It generates a public, read-only share link (opaque token, no account required). The popover shows: the generated URL with a copy action, an optional expiry picker, and a list of active share links for this view/doc with revoke controls. Share link recipients see a stripped-down shell: the canvas or doc in read-only mode, the strata breadcrumb, and the Presentation lens active by default — no Navigator, no editing controls, no account chrome.

---

## 13. Auth & account

**Login** is a full-page centered card (shadcn Card) with: external OIDC buttons (GitHub, Google — as configured per deployment), and a fallback email/password form for solo self-hosters. No custom registration flow beyond what ASP.NET Core Identity provides. After login, the user lands on their workspace's project list. **Account settings** (accessible from a user avatar menu in the top bar): display name, connected providers, and sign-out.

---

## 14. Workspace & project management

The **top bar** project selector is a dropdown listing projects in the current workspace. A **workspace settings** page (reached from the avatar menu) shows: members with roles (owner / editor / viewer), an invite flow (email or link), and project CRUD. Project settings (from a gear icon in the project selector) cover: project name, description, danger-zone delete. Workspace switching (for users in multiple workspaces) uses a secondary dropdown above the project list.

---

## 15. AI / MCP

**"Ask AI"** is a command-palette action (`⌘K` → "Ask AI…") that opens a focused chat panel (shadcn Sheet, right side, replacing the detail panel temporarily). The user types a natural-language query about the model ("What depends on Orders API?", "Who owns the payment system?"). Responses render inline as structured answers with deep-links to referenced elements (clicking navigates the canvas). The AI reads the model via MCP tools; responses are read-only in v1 (no model mutations from the chat). A subtle indicator shows when the MCP query is in flight.

---

## 16. Export

**Export** is available from the command palette and from a project-level menu. v1 exports:

- **Model export** (YAML/JSON): the full serialized model (elements, relationships, messages, schemas) per the SDD's canonical format. Layout is exported as a separate file to keep semantic exports clean.
- **View export** (PNG/SVG): a static snapshot of the current canvas view, respecting the active lens and zoom level.

Export format selection uses a simple dialog with radio buttons (YAML / JSON for model; PNG / SVG for view).

---

## 17. Interaction & keyboard

Built for a technical audience: `⌘K` command palette as the spine; shortcuts for create, descend/ascend levels, toggle lenses, tidy layout, enter/leave draft, focus search. Multi-select + box-select for bulk move/tag/status. Every command-palette action has a discoverable shortcut. Consistent verb naming: the control that says "Merge draft" produces a "Draft merged" toast.

---

## 18. States

- **Empty project:** an invitation, not a void — "Start with a system. Draw a connection to add the next one." with the create affordance front and centre.
- **Empty view / no children yet:** "Nothing inside *Orders API* yet — drill in starts here."
- **Errors:** in the interface's voice, specific and actionable — e.g. a save conflict reads "This changed in another session. Reload to see the latest, then reapply your edit," not a stack trace or an apology.
- **Optimistic concurrency conflict (409):** when two sessions edit the same element, the second save shows an inline toast with the conflict message and a "Reload" action — the user re-applies their edit on the fresh state.
- **Loading:** skeleton placeholders in the Navigator and detail Sheet; the canvas shows a centered spinner on initial model load; subsequent mutations are optimistic (instant UI update, rolled back on failure).

---

## 19. Component division & performance

- **shadcn/Tailwind v4** for all chrome: top bar, Navigator, detail Sheet, lens bar, dialogs, command palette, review/conflict panels, docs editor.
- **Bespoke lightweight nodes** on the canvas (Tailwind-styled, not shadcn primitives) — for the custom C4 visual language *and* render perf. Architecture levels are small (tens of nodes), but the label-chip tier and memoised nodes keep pans/zooms smooth. Don't mount heavy shadcn components inside React Flow nodes.

---

## 20. Quality floor

Visible keyboard focus throughout; full keyboard operability of the canvas (select/move/descend); `prefers-reduced-motion` respected on all transitions; status and diff colours meet contrast on both themes and are never the *only* signal (paired with dashed/struck/iconography for colour-blind safety).

---

## 21. Open / to refine

- Exact display/mono typeface picks (open-licensed options preferred for self-host).
- Whether docs open inline (Sheet) vs. a dedicated full-width editor by default.
- Auto-layout flavour (layered vs. orthogonal) per diagram type — tune against real models.
- Minimap for large levels (likely yes, low cost via React Flow).
