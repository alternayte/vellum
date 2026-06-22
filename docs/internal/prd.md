# Vellum — Product Requirements Document

> Working title — rename freely. Status: **Draft v0.1**. License: **MIT**. Companion doc: `SDD.md` (technical design, written next).

---

## 1. Summary

A self-hostable, open-source web app for **designing, planning, and documenting software architecture**.

It combines three things that today require separate, mostly-paid tools:

1. **C4-first visual modelling** — polished, interactive, zoomable diagrams, as good as or better than IcePanel.
2. **A design-and-plan workflow** — a Git-style *draft → review → merge* loop over the architecture, decision records, and current-vs-future status.
3. **Integrated documentation** — general technical and product docs (not just ADRs) with **live diagrams embedded** from the same model.

One model is the single source of truth. Every diagram, doc, and view is a *projection* of it — change something once and it updates everywhere.

An optional **event/message overlay** (the genuinely useful part of EventCatalog) sits on top as a toggleable lens — primary product is design-first; the catalog is a secondary bridge.

---

## 2. Problem

Architecture work today forces a bad trade-off:

- **Pretty but manual** (IcePanel, Lucidchart, Miro): great visuals, but the model is hand-maintained and drifts from reality, and it's paid SaaS.
- **Accurate but utilitarian** (EventCatalog, Structurizr): code/spec-driven and accurate, but weak design/planning UX and plainer visuals.

In both cases the recurring failures are: **no upfront design**, **misaligned teams**, **scattered context**, and **docs disconnected from reality**. Nothing closes the loop from *a decision* → *a planned change* → *the affected elements* → *the living diagram and docs*.

**Why this matters now:** No open-source tool combines C4 visual modelling, a design-and-plan workflow, and integrated documentation. The closest paid alternatives (IcePanel, Structurizr Cloud) charge per-seat SaaS fees, locking teams into vendor-hosted data with no self-host option and no export-to-own-your-data story. Teams that want to own their architecture tooling today must cobble together Structurizr DSL (no planning UX), static Markdown docs (no live diagrams), and a whiteboard (no model). Vellum fills this gap as MIT-licensed, self-hostable, single-deployment software.

---

## 3. Goals & Non-goals

**Goals (v1)**
- Model a real system across C4 levels with visuals at least on par with IcePanel.
- Make *planning future state* first-class: draft, review, merge, with decisions captured.
- Author real technical/product docs with embedded, always-current diagrams.
- Self-hostable from a single deployment; MIT; own your data (full export).
- AI-queryable and AI-authorable model out of the box (MCP).

**Non-goals (v1) — deliberately cut to stay lean**
- Real-time multiplayer editing (live cursors / CRDT). Accounts + async editing + share links only.
- Deep RBAC / SSO / SOC2 / enterprise governance reports.
- 15+ auto-generators from live infrastructure. (A small AsyncAPI/OpenAPI import comes later.)
- Generating architecture *from* deployed infra. (Drift detection is a later stretch, not v1.)

---

## 4. Users

- **Architect / tech lead (primary author).** Designs and plans systems, owns the model, writes docs and ADRs, opens drafts.
- **Engineers (contributors / consumers).** Read the model, comment, propose changes via drafts, consume schemas/docs.
- **Product & stakeholders (readers).** View high-level diagrams and docs, comment; never need to understand notation.

---

## 5. Principles (locked decisions — constraints for the SDD)

1. **Model is the single source of truth.** Diagrams and docs are projections, never copies.
2. **DB is the source of truth, but the model is fully serializable** to clean YAML/JSON — for export, diffing, AI/MCP, and "own your data."
3. **C4 is the abstraction model, not a visual notation.** C4 deliberately doesn't prescribe visuals, so we own the design language and can out-design existing tools.
4. **Event-sourced model.** Drafts, history, review diffs, and impact analysis all fall out of one append-only mechanism.
5. **Hosted, but self-hostable and lean.** No realtime/CRDT in v1.
6. **Visual quality is a feature, not a finishing touch.** Match or beat IcePanel.
7. **Events/messages are a lens, not a parallel ontology.** C4 structure stays primary and uncluttered.
8. **React Flow is the canvas substrate.** We build semantic zoom, projection, and layout on top of it; the C4 visual language and interaction model are ours.

---

## 6. Conceptual model (the domain)

This is the heart of the product. Physical schema is deferred to the SDD; this defines *what exists* and *how it relates*.

**Element** — a node in the model. `kind ∈ {actor, system, app, store, component}`. Fields: name, description, technology, owner, tags, **status** (`current | planned | deprecated | removed`), `parent` (containment). Containment is what produces C4 zoom: system → app → component.

**Relationship** — a directed connection between elements. Fields: from, to, label, technology, optional `message` ref.

**Message** *(overlay)* — a first-class event/command node referencing a **Schema**. Producer → message → consumer(s). Hidden by default; shown when the event lens is on. This is the EDA bridge.

**Schema** — a versioned contract (JSON Schema / AsyncAPI / OpenAPI fragment) attached to a message or service.

**View** — a saved projection of the model: a root element (the level you've zoomed into), the visible elements, active filters/lens, and an active flow. Many views, one model.

**Layout** — per-view positioning sidecar (`elementId → {x, y}`, edge routing). Kept separate from the semantic model so diffs and AI stay clean.

**Flow** — an ordered sequence of steps, each referencing an element or relationship, overlaid on a view. "One diagram, many narratives."

**Draft** — a named branch of the model, stored as a **diff overlay** of element/relationship changes on top of main. Renders as `base + overlay`. Reviewable as a changeset; mergeable into main. Backed by the event log.

**Decision record (ADR)** — a doc that captures *why* a change was made, anchored to the draft and the elements it affected.

**Doc** — a general authoring unit. Long-form Markdown/MDX. Two homes: (a) attached to an element, (b) standalone pages organised in spaces. Supports ADRs, RFCs, runbooks, and arbitrary technical/product docs. Can **embed live views** and cross-link elements.

**Tag / Owner / Team** — metadata used as visual lenses and filters (tag color dots, owner avatars), without duplicating diagrams.

**Workspace → Project** — tenancy and grouping. **User** — account. **Share link** — public, read-only projection of a view or doc.

---

## 7. Features

**A. Modelling & canvas** `[Must — Phase 1]`
- C4 levels with **semantic zoom** (detail swaps as you drill context → app → component).
- Drag-drop model-as-you-draw; new objects added to the model, reusable across views.
- Auto-layout (elk/dagre) with manual override persisted per view.
- Semantic zoom, projection, and layout logic are ours; canvas renders via React Flow (see §5, Principle 8).

**B. Lenses & overlays** (all just filters over one model)
- Metadata (technology, tags, owner, description) shown as subtle visual encoding. `[Must — Phase 1]`
- **Status** lens: current / planned / deprecated / removed. `[Must — Phase 1]`
- **Flows**: step-through narratives over an existing view. `[Should — Phase 1–2]`
- **Event/message** lens: expand a relationship into producer → message → consumer; messages become hubs. `[Could — Phase 4]`

**C. Design & planning** (the core "design and plan")
- **Status** — encode current vs future without a full draft. `[Must — Phase 1]`
- **Drafts** — fork the model, design a future state. `[Should — Phase 3]`
- **Review & merge** — view the draft as a changeset, comment, merge into main. `[Should — Phase 3]`
- **Decision records** — capture decisions, anchored to drafts and affected elements. `[Should — Phase 3]`
- **Feedback** — comments / annotations on elements and drafts. `[Should — Phase 3]`
- **Ask AI** — LLM feedback on a design via MCP. `[Could — Phase 4]`

**D. Documentation** `[Should — Phase 2]`
- General technical + product docs authoring (Markdown/MDX), not just ADRs.
- Per-element docs and standalone doc spaces.
- **Embedded live views** — diagrams in docs stay current with the model.
- Element cross-linking; the decision → draft → element → diagram chain is navigable.

**E. Sharing & collaboration**
- Accounts, workspaces, projects. `[Must — Phase 0]`
- Public read-only **share links** for views and docs. `[Should — Phase 2–3]`
- Embeds in external tools. `[Won't — v2]`

**F. AI / MCP** `[Could — Phase 4]`
- MCP server: query the model in natural language ("what depends on X?", "who owns Y?").
- Agent-authorable model (Claude Code can read/write the serialized model).
- `llms.txt`-style export for agent discovery.

### Feature priority summary

| Priority | Scope | Phase |
|----------|-------|-------|
| **Must** | Canvas + C4 modelling, semantic zoom, metadata + status lenses, accounts + workspaces | 0–1 |
| **Should** | Documentation + embedded views, drafts + review + merge + ADRs + comments, share links, flows | 2–3 |
| **Could** | Event/message lens, schema attach, MCP query + model export, Ask AI | 4 |
| **Won't (v1)** | Embeds in external tools, real-time collaboration, deep RBAC/SSO, drift detection, schema versioning | v2 |

The **Must** set alone (Phases 0–1) forms a shippable first increment: a C4 modelling tool with semantic zoom, status encoding, metadata lenses, and multi-user workspaces.

---

## 8. Releases

Two releases only — **v1** (a strong design-first tool) and **v2** (the complete product, at parity with or beating IcePanel). The items under v1 are internal build order, not separate releases.

### v1 — build order

- **Phase 0 — Foundation.** Meta-model, DB, event log, serialization, projection engine.
- **Phase 1 — Modelling (shippable design tool).** Canvas, C4 levels, semantic zoom, metadata, **status**, layout, views. *Status ships before Drafts — it covers current-vs-future almost for free.*
- **Phase 2 — Docs.** Authoring + embedded live views + cross-linking.
- **Phase 3 — Planning.** Drafts → review → merge, decision records, comments. *(Second-biggest build after the canvas — lightweight version control over the model.)*
- **Phase 4 — Bridge & AI.** Basic message/event overlay + schema *attach*, MCP query + model export.

### v2 — Complete product (parity with or better than IcePanel)

One release, complete. Everything v1 deliberately deferred, plus the wedges that make it *beat* IcePanel.

**Parity with IcePanel**
- **Real-time collaboration** — presence + live multiplayer editing. *Heaviest single item; see note.*
- **Living embeds** in Notion / Confluence / etc. — a live iframe of a share-tokened view.
- **REST API / SDK** — keep the model in sync with code programmatically.
- **Deployment & networking diagram types** (C4 supplementary), beyond structural + dynamic.
- **Version timeline / "time machine" UI** over the event log (v1 has the data; v2 has the view).
- **Team management** — workspaces, roles (viewer/editor/admin), SSO/OIDC.
- **Visual polish** — tech-logo/icon library, themes, PNG/SVG/PDF export, keyboard-driven UX.

**Beats IcePanel (our wedges)**
- **Full schema/event layer** — AsyncAPI/OpenAPI/Protobuf/Avro import, schema versioning, breaking-change detection.
- **Drift detection** — diff the *designed* model against *imported reality*. The loop neither tool closes.
- **Deep docs** — the MDX authoring + live-embed system at full strength (IcePanel has no real docs authoring).
- **Full AI authoring** — the agent builds and edits the model, not just comments on it.

> **Note on realtime.** True CRDT multiplayer (Yjs) is the costliest v2 build. For an *architecture* tool — far less simultaneous than a whiteboard — *presence + soft-locking* ("someone is editing this") may deliver 90% of the value for a fraction of the cost. Decide deliberately in the SDD.

---

## 9. Success criteria

- Model a real system across 3 C4 levels, looking at least as good as IcePanel.
- Author a doc with a live-embedded diagram that updates when the model changes.
- Fork a draft, propose a future state, review the changeset, and merge it.
- Self-host via a single deployment; export the entire model to YAML.
- MCP answers "what depends on X?" correctly against the live model.

---

## 10. Non-functional requirements

### Authorisation model (v1)

Derived from the §4 personas and §5 tenancy model (Workspace → Project → membership):

| Role | Model (elements, relationships) | Views & layouts | Drafts | Docs | Workspace/project admin | Share links |
|------|------|------|------|------|------|------|
| **Owner** | Full CRUD | Full CRUD | Create, review, merge, delete | Full CRUD | Create/delete project, manage members | Create/revoke |
| **Editor** | Create, read, update | Create, read, update | Create, review, merge own | Create, read, update | Read only | Create |
| **Viewer** | Read | Read | Read, comment | Read | Read only | — |
| **Public (share link)** | Read (scoped) | Read (scoped) | — | Read (scoped) | — | — |

### Other NFRs — targets needed

| Area | What to specify | Status |
|------|----------------|--------|
| **Performance** | Canvas interaction p95 latency (pan/zoom/select), command processing p95 (add/edit element), page load time | *Needs owner input* |
| **Availability** | Uptime target for hosted offering; self-host has no SLA (operator's responsibility) | *Needs owner input* |
| **Data retention & privacy** | Retention policy for event log and snapshots; GDPR applicability; right-to-deletion scope; PII handling in model data | *Needs owner input* |
| **Observability** | What to log (commands, auth events, errors), what to alert on, SLIs/SLOs for hosted | *Needs owner input* |
| **Capacity** | Expected concurrent users at launch, model size limits (elements per project), growth projections | *Needs owner input* |

---

## 11. Dependencies & risks

### Key technology dependencies

| Dependency | License | Role | Risk note |
|------------|---------|------|-----------|
| React Flow | MIT | Canvas rendering | Actively maintained; custom node/edge API is stable |
| elkjs / dagre | MIT / MIT | Auto-layout algorithms | Mature, low churn |
| PostgreSQL 17 | PostgreSQL License | Only datastore | Widely supported; no exotic extensions required |
| MDX ecosystem | MIT | Doc authoring & rendering | JS-only; sandboxing user-authored MDX is a known risk (see §12.3) |
| ASP.NET Core / .NET 10 | MIT | Backend runtime | LTS track; GA Nov 2025 |

### Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|-----------|--------|------------|
| R1 | React Flow rendering limits at high node counts (100+ per level) | Low | Medium | C4 levels are naturally small (tens of nodes); label-chip LoD reduces render cost; benchmark early in Phase 1 |
| R2 | Event store growth on active projects degrades snapshot load times | Medium | Medium | Snapshot-loading pattern avoids replay; monitor size; add partitioning/archival if measured need arises |
| R3 | MDX sandboxing insufficient for multi-tenant user-authored content (code injection) | Medium | High | Strict component whitelist; no arbitrary code execution; security review before Phase 2 launch |
| R4 | Three-way merge engine complexity exceeds estimates (Phase 3) | Medium | High | Identified as riskiest code; heavy test coverage with every conflict case; keep merge rules simple (per-entity, not per-field in v1) |
| R5 | Solo-developer capacity bottleneck vs. v1 scope | High | High | Phase structure allows shipping Phase 1 independently as a useful product; ruthless scope control; defer Should/Could items if needed |
| R6 | Model-as-single-aggregate creates write contention at scale | Low (v1) | Medium | Per-project edit concurrency is low; optimistic concurrency sufficient for v1; Yjs in v2 addresses collaborative editing |

---

## 12. Decisions & open questions

| # | Question | Status | Owner | Resolve by |
|---|----------|--------|-------|------------|
| 1 | **Name** — candidates: Strata, Vellum, Cairn, Keystone, Meridian, Atrium. Domain/trademark check needed. | Open | *Needs owner* | Before public launch |
| 2 | **Auth** — *Principle locked:* OIDC-first (self-hosters bring GitHub/Google/their IdP) plus a simple built-in option for solo self-host; never hand-roll password/session crypto — use a vetted library. Specific library/flow decided in the SDD (stack-dependent). | Decided (principle) | — | SDD finalises implementation |
| 3 | **Docs format** — **Locked: MDX** (needed for live-view components). SDD must sandbox/whitelist user-authored MDX (multi-tenant code-exec risk). | Decided | — | — |
| 4 | **Message overlay depth** — **Decided: shallow in v1** (message nodes + schema attach + producer/consumer wiring + canvas lens). Versioning, breaking-change, and import move to v2. | Decided | — | — |
| 5 | **Embeds** — **Decided: deferred to v2**, using a **live iframe** of a share-tokened view. (Static PNG/SVG export is a cheap v1 nicety; living diagrams are the point, so snapshots aren't the embed story.) | Decided | — | — |
| 6 | **Tech stack** — SDD commits .NET 10 / C# backend + React SPA frontend + PostgreSQL. Original PRD recommendation was TypeScript/Next.js; the SDD's rationale (event-sourcing kernel, exhaustive unions, single-binary deploy) drove the switch. | Decided (SDD) | — | — |
| 7 | **NFR targets** — Performance (p95 latencies), availability (SLA), data retention, observability, capacity targets are unset (see §10). | Open | *Needs owner* | Before Phase 1 build |
| 8 | **Timeline** — No dates or duration estimates per phase. | Open | *Needs owner* | Before Phase 0 start |
| 9 | **Success metrics** — §9 criteria are demo scenarios; quantified metrics with baselines and targets are needed (see §3). | Open | *Needs owner* | Before Phase 1 build |
