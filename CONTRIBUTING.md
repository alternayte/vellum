# Contributing

## Before you start

- For a change larger than a fix, open an issue first and describe the change.
- Each contributor signs the [Contributor License Agreement](CLA.md) once. The CLA check on
  your first pull request gives the sentence to post as a comment. The agreement lets the
  project owner license Vellum under the AGPL and under other terms, including a hosted
  service. You keep the copyright of your work.

## Build and test

You need the .NET 10 SDK, Node.js, Docker and `just`.

- `just up` starts Postgres. `just run` starts the API. `just dev-web` starts the web client.
- `just check` is the gate. A pull request passes it before review.
- `just test` runs the backend tests. They start Postgres in Docker.

`AGENTS.md` holds the stack rules and the domain words. Read it before you change code.

## Rules for a change

- A migration that a release holds never changes. Add a migration with
  `just migration <name>`. `checks/migrations-frozen.sh` enforces this.
- A change to an event type keeps old events loadable. `events.lock` and
  `EventContractTests` enforce this.
- After a change to the HTTP API, run `just openapi` and commit
  `src/Vellum.Web/openapi.json`.
- Describe each user-visible change under `## Unreleased` in `CHANGELOG.md`.
