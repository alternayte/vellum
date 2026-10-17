---
title: Import a repository and read the result
description: Scan the Orders fixture repository through a local GitHub stand-in, see the diagram it gives, and read each fact with its source lines.
---

In this tutorial you scan a repository, see the diagram that Vellum makes of it, and read where each fact comes from. The repository is the Orders fixture that the Vellum tests use. You need no GitHub account and no AI provider.

## Before you start

- Complete [Run Vellum on your machine](/tutorials/self-host-quickstart/). You are signed in as the owner of a workspace.
- You have the Vellum source on your machine, and Node.js 22 or later.

## What you scan

The directory `tests/Vellum.Tests/Fixtures/Repos/orders` holds a small system: an ASP.NET Core API, a .NET billing service, a Go inventory worker, an OpenAPI file, an AsyncAPI file and a Compose file.

Vellum reads repositories through the GitHub REST API. The script `examples/github-standin/server.mjs` answers the calls of that API from directories of your machine. It serves the fixture as the repository `acme/orders`:

| Ref | Directory | Content |
|---|---|---|
| `main` | `tests/Vellum.Tests/Fixtures/Repos/orders` | The system with a transactional outbox. |
| `remove-outbox` | `tests/Vellum.Tests/Fixtures/Repos/orders-outbox-removed` | A change that removes the outbox and gives one field of `OrderCreated` a new name. |

The stand-in also serves pull request #1, **Remove the outbox**, from `remove-outbox` into `main`.

To scan a repository on GitHub in place of the fixture, do not do the next two sections. Give `owner/name` of a public repository in the import form. [Connect GitHub](/how-to/connect-github/) shows how to read a private repository.

## Start the stand-in

1. In the directory of the Vellum source, start the stand-in. Keep it running in its own terminal.

   <!-- snippet: syntax -->
   ```sh
   node examples/github-standin/server.mjs
   ```

   It prints one line for each ref, with the commit SHA and the number of files, and then its address: `http://127.0.0.1:5299`.

2. In a second terminal, check that it answers.

   <!-- snippet: live -->
   ```sh
   curl -s http://127.0.0.1:5299/repos/acme/orders | jq -r '.full_name, .default_branch'
   ```

   The command prints `acme/orders` and `main`.

The commit SHA of a ref is a hash of the files in its directory. The same files always give the same SHA.

## Point the workspace at the stand-in

1. On the home screen, select your workspace, then select **Settings** next to its list of projects.
2. Select **GitHub**.
3. In **API base URL**, give the address of the stand-in as Vellum can reach it:
   - Vellum runs in Docker Compose on Docker Desktop: `http://host.docker.internal:5299`.
   - Vellum runs on your machine with `dotnet run`: `http://localhost:5299`.
4. Select **Save changes**.
5. Select **Test connection**. Vellum shows "Connected without a token: public repositories only." and the number of requests that remain.

The local profile accepts an address on a private network and plain `http`. The hosted profile refuses both by default.

On Linux with Docker Engine, the name `host.docker.internal` does not exist in a container by default. Start the stand-in with `--host 0.0.0.0`, and give the address of the Docker bridge of your machine, for example `http://172.17.0.1:5299`.

When you complete the tutorials, put the value `https://api.github.com` back.

## Scan the repository

1. On the home screen, select your workspace, then select **+ New Project**. In **Project name**, give `Orders import`. Select **Create**. The project opens.
2. Select **Import** in the left toolbar. The **Sources** panel opens.
3. In **Repository**, give `acme/orders`.
4. In **Read**, select **Branch**. In **Branch or tag**, give `main`.
5. Leave **Only these folders** empty.
6. Select **Scan repository**.

The scan shows "Listing files", then "Reading files", then "Scan complete".

A scan reads one commit. It runs no code from the repository.

## See the diagram

When the scan completes, Vellum adds the 21 facts that it found to the model. The model was empty, so every fact is an addition, and no step needs you. **Needs you** in the **Sources** panel reads "Nothing needs you".

1. Close the **Sources** panel. The canvas shows the top level of the view with the system `orders`. The auto layout placed it.
2. Right-click `orders` and select **Drill into**. The canvas shows `Orders.Api`, `Billing`, `inventory-worker` and `postgres`.
3. Select **Contracts** in the left toolbar. **Operations**, **Channels** and **Subscriptions** list the rest of the 21 facts.

![The canvas at the level Context, orders. A frame with the name orders holds the apps Billing (dotnet), inventory-worker (go) and Orders.Api (dotnet) and the store postgres. Each card shows its kind and its technology. No line connects them.](../../../assets/screenshots/canvas-orders-system.png)

| Kind | Facts |
|---|---|
| Element | `orders`, `postgres`, `Billing`, `inventory-worker`, `Orders.Api` |
| Channel | `billing.order-created`, `inventory.order-created`, `order-events`, `order-events.dlx` |
| Schema | `OrderCreated`, `CreateOrder`, `Order`, `OrderLine` |
| Operation | `GET /healthz` (two services), `GET /orders/{id}`, `POST /orders` |
| Binding | `inventory.order-created` bound to `order-events` |
| Subscription | The consumer of Billing and the consumer of the inventory worker |
| Message | `OrderCreated` |

## Read where a fact comes from

1. Select the card `Orders.Api`. The detail panel opens.
2. Read **Truth**. The label is **Observed in source**. The provenance gives the file `orders-api/Orders.Api.csproj` at the commit of the scan, your account as the person who accepted it, and the time.

Each fact has the label `observed-in-source`, the commit, the file and the lines, and your account. You started the scan, so you are the person who accepted its facts.

The link **GitHub** on a source line opens the file on the web address of the repository. The stand-in serves the API only, so the link does not open a page in this tutorial.

Three things are true about this model:

- **It holds what the source declares, and nothing more.** The source shows that `POST /orders` exists. It does not show how long the call takes, or that it works.
- **It has no behaviour.** The scan found no scenario. Lines in source code do not state the order of a flow. You declare a flow, or the assistant proposes one.
- **It has gaps.** The fixture binds the queue of Billing by a MassTransit convention that is not in the source. Vellum proposes no binding for it. A person adds that binding by hand in **Contracts**.

## Scan again

1. In the **Sources** panel, select **Scan again** on the Source `acme/orders`.
2. Wait for "Scan complete". **Needs you** still reads "Nothing needs you", and the canvas does not change.

A second scan of the same commit compares the source with the model and finds no difference.

## Edit a fact and scan again

1. Select the card `postgres`. In the detail panel, change **Technology** to `PostgreSQL 16`.
2. Select **Scan again** on the Source.

The source still says `postgres`, but it did not change since the first scan. Vellum keeps your edit and puts nothing in **Needs you**. When a later commit of the repository changes a fact that you edited, the change waits in **Needs you**, and the card shows the badge **needs you**. Imported code never overwrites what a person decided.

## What you did

- You scanned one commit of a repository with no GitHub account.
- Vellum added what it found, labelled `observed-in-source`, with you as the acceptor, and drew it with the auto layout.
- You read where a fact comes from, and saw that a scan keeps your edit.

## Next

- [The promise loop: from a question to evidence](/tutorials/promise-loop/) uses this project and the ref `remove-outbox`.
- [Import a repository](/how-to/import-a-repository/)
- [Check the impact of a change](/how-to/check-change-impact/)
- [Extraction rules](/reference/extraction-rules/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
