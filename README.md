# Vellum

Vellum is an architecture workbench that you host yourself. You draw a system, declare
its APIs and events, play a flow through it, and state promises that the design must keep.
Vellum then gives a coding agent a specification to build from.

![The Vellum canvas with the model of an online shop. The actor Customer uses the system Storefront. Storefront places orders with the system Orders. Orders is expanded: a frame holds the apps Orders API and Order worker and the store Orders DB. Orders requests payment from Payments and sends paid orders to Fulfilment. Payments authorises cards with the external system Card network. Each card shows the kind, the technology and a description, and each line has a label.](site/src/assets/screenshots/canvas-online-shop.png)

Documentation: https://vellum-arch-docs.pages.dev

## What it looks like

A scenario plays on the canvas, one event at a time. Here Billing fails to handle a
message, and one of the two expectations fails.

![A scenario plays on the canvas. The trace dock at the bottom shows event 5 of 7: Billing fails to handle the message. The canvas rings Orders.Api and Billing in red inside the frame of the system orders.](site/src/assets/screenshots/trace-dock-playback.png)

A promise has a stable ID and obligations. Each obligation shows its evidence: passed,
failed or not run.

![The promise ORD-014 with three obligations. ORD-014/O1 has the label Passed, with the faults played and the trace hash. ORD-014/O2 has the label Not run, because the plan names no evidence source. ORD-014/O3 has the label Failed against the scan of the branch remove-outbox.](site/src/assets/screenshots/promise-obligations-results.png)

A scan of a pull request shows each flow step that the change can affect, with the source
lines that give the label.

![The impact of pull request #1 on acme/orders. Each step of the flow Place an order has the label Definite or Possible and the source lines that give it.](site/src/assets/screenshots/impact-pull-request-steps.png)

## What it does

- **Model.** Systems, apps, stores and actors on a canvas, with C4-style drill-down.
  Operations, channels, messages and JSON schemas are part of the model, not drawings.
- **Trace.** A scenario of calls, publishes and writes plays step by step with its
  payloads and faults. A trace shows the declared design. It does not run your code.
- **Import.** A Source is any origin of evidence: GitHub, GitLab, Bitbucket, Azure DevOps,
  a local folder, a zip or tar.gz upload, a push from the `vellum scan` CLI, Structurizr,
  Backstage, ADRs, an OpenTelemetry trace export or New Relic. Each import is a proposal
  with file and line. A person accepts or rejects each change.
- **Truth labels.** Each fact is `declared`, `observed-in-source`, `inferred`, `measured` or
  `unknown`. Vellum never promotes an inference to a fact without a person.
- **Promises.** A requirement with a stable ID such as `ORD-014`, approved by an owner,
  with obligations that checks give evidence for. A result goes stale when its inputs change.
- **Impact.** A scan of a branch or a pull request labels each changed fact, flow step and
  promise `definite`, `possible` or `unknown`. CI can read a compact report.
- **Review.** Fixed rules report concerns about resilience, contracts and truth labels.
  Decisions (ADRs) settle findings.
- **Drafts and variants.** Change the model in a draft, read the diff and merge it.
  Compare variants and named revisions.
- **Share and comment.** Read-only share links, live or pinned to a revision, and embeds.
  Comment threads on elements, flows and promises. Export SVG, PNG, Mermaid and
  Structurizr DSL.
- **Build.** Export a standalone `SDD.md` and a bundle with OpenAPI, AsyncAPI, scenarios and
  promises. The same model gives the same bytes.
- **Agents.** A coding agent connects over MCP at `/mcp`. It reads the model, proposes
  changes and exports the specification. An agent token cannot approve anything.
- **Costs and stress.** Cost estimates from prices that you enter or fetch from the Azure,
  AWS and Google Cloud price APIs. Load simulation of a scenario with a fixed seed.

An AI provider is optional: OpenAI, Anthropic, Gemini or any OpenAI-compatible server.
Every feature except the assistant works without one.

## Quick start

You need Docker with the Compose plugin.

```sh
mkdir vellum && cd vellum
curl -fsSLO https://github.com/alternayte/vellum/releases/latest/download/docker-compose.yml
docker compose up -d --wait
docker compose logs vellum | grep "VELLUM SETUP CODE"
```

Open `http://localhost:8080`. Give your name, an email, a password and the setup code, then
create the first workspace.

The Compose file publishes the ports on `127.0.0.1` only. To serve Vellum to other people,
put it behind HTTPS first. See
[Run in hosted mode](https://vellum-arch-docs.pages.dev/how-to/run-in-hosted-mode/) and
[Upgrade](https://vellum-arch-docs.pages.dev/how-to/upgrade/).

## The `vellum scan` CLI

`vellum scan` reads code on the machine that holds it and pushes the facts and their
citations to Vellum. It sends no file content. Download the file for your platform from the
[latest release](https://github.com/alternayte/vellum/releases/latest). See
[Push evidence from CI or a laptop](https://vellum-arch-docs.pages.dev/how-to/push-evidence-from-ci-or-a-laptop/).

## Connect a coding agent

1. In the app, open the workspace settings, then **Agent tokens**. Create a token.
2. Give the token to your MCP client as a bearer header. For Claude Code:

```sh
claude mcp add --transport http vellum http://localhost:8080/mcp \
  --header "Authorization: Bearer <token>"
```

`http://localhost:8080/llms.txt` describes the agent interface. The agent skill is in
`skills/vellum/SKILL.md`.

## Develop

You need the .NET 10 SDK, Node.js, Docker and `just`.

```sh
just up        # the database only
just run       # the API
just dev-web   # the web client, http://localhost:5173
```

`just check` is the gate before a commit: build, code style, lint, type checks, the web
tests and the docs checks. `just test` runs the backend tests, which start Postgres in
Docker. `just docs` serves the documentation site at http://localhost:4321.

| Path | Content |
|---|---|
| `src/Vellum` | The API. A modular monolith on .NET with Postgres. One folder for each feature under `Modules/`. |
| `src/Vellum.Web` | The web client. React and React Flow. |
| `src/Vellum.Cli` | The `vellum scan` CLI. |
| `tests/Vellum.Tests` | Backend tests and fixture repositories. |
| `site` | The documentation site. |
| `examples` | The Orders reference model and a stand-in for the GitHub API. |
| `skills/vellum` | The skill that tells a coding agent how to use Vellum. |

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md). Each contributor signs the
[Contributor License Agreement](CLA.md) once, on the first pull request. To report a
vulnerability, follow [SECURITY.md](SECURITY.md).

## Licence

Vellum is free software under the [GNU Affero General Public License v3.0](LICENSE).
`skills/vellum` and `examples` are under the [Apache License 2.0](skills/vellum/LICENSE), so
that you can copy them into your own projects.
