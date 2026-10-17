# Vellum

## What this is
Vellum is a self-hostable architecture workbench: model a system, trace flows, declare promises and export an agent-buildable SDD.
It is a .NET 10 modular monolith in src/Vellum with a React 19 SPA in src/Vellum.Web and a docs site in site.

## Run
- `just up` starts Postgres only; then `just run` starts the API and `just dev-web` starts the web client.
- `just up-all` starts the whole app in containers at http://localhost:8080.
- `just seed` creates the dev user dev@vellum.local and its dev data.

## Test
- `just check` is the gate before a commit: build with warnings as errors, format, OpenAPI diff, web build, lint, typecheck, vitest and docs checks.
- `just test` runs the backend suite without Category=Slow; `just test-slow` runs the slow tests.
- Backend tests use xUnit with a Testcontainers Postgres, so Docker must run.
- `just docs-verify-live` checks the docs snippets against a running Vellum.

## Stack rules
- The web client uses npm and Vite, not Bun.
- The event store is Deedbox; do not replace it.
- One EF Core AppDbContext holds every module's tables, each module in its own schema; add a migration with `just migration <name>`.
- After an API change, run `just openapi` and commit `src/Vellum.Web/openapi.json`; the gate fails when it differs from the build output.

## Domain words
- truth label: the class of a fact by its origin, as the FactStatus enum defines.
- promise: a human-approved architectural claim with a stable ID, such as ORD-014.
- obligation: a check whose evidence decides a promise.
- draft: a branch of the model that merges into main.
- scenario: a flow of steps played on the canvas against declared contracts.
- Source: a configured origin of architecture evidence for a project, with a kind such as github, directory, archive, agent-push, model-file, traces or newrelic. Avoid: repo, integration, connector.
- Snapshot: one immutable import of a Source, identified by the SHA-256 of its manifest of path, size and git blob SHA. Avoid: scan result, import, version.
- source binding: an explicit pair of Source and external key that links a model element to its counterpart in that Source. Avoid: alias, mapping, match.
- share link: a read-only URL to a project or view for people without an account, either live or pinned to a revision. Avoid: public link, guest access.
- Commenter: the workspace role between Viewer and Editor that can comment but not edit. Avoid: reviewer.
- detached: the status of a comment thread whose target was deleted. Avoid: orphaned.
- ghost layer: the dashed canvas overlay that shows a pending AI proposal, where a person accepts or rejects its items. Avoid: preview, draft overlay, suggestion layer.
- Needs you: the list of rescan changes to facts a person edited, each waiting for that person's decision. Avoid: conflicts, review queue, inbox.
- derived line: a line the canvas and the exports draw between two visible cards for relationships whose ends are hidden inside them; it is never stored. Avoid: implied relationship, lifted edge, rolled-up edge.
- not seen in latest scan: the status of a model fact that the latest Snapshot of its Source no longer holds. Avoid: stale, removed.
