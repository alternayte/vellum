---
title: Start a project
description: Get a first diagram into an empty project from a folder, a GitHub repository, a description, typed lines or the sample.
---

This guide shows you the three starts of an empty project. You need the editor role in the workspace.

A project whose model has no element shows the heading **Start your diagram** and three cards:

- **Choose folder / GitHub URL** reads code.
- **Describe it / Type it** takes your words.
- **Open the sample** loads the Orders example.

Below the cards, the link **Add a system** opens the dialog **Add Element**, as the blank canvas did.

The starts show on the main model only. An empty draft or a named revision shows the canvas.

## Choose a folder

1. Select **Choose folder**. The folder dialog of the browser opens.
2. Select the root folder of a repository. The browser can ask you to confirm the upload of the files.

The browser reads a copy of the files. It never writes to the folder, and it keeps no access to the folder after the upload.

The browser sends only the files that a scan reads:

- C# code and project files (`.cs`, `.csproj`), Go code and `go.mod`.
- JSON and YAML files (`.json`, `.yaml`, `.yml`), such as OpenAPI, AsyncAPI, Compose and Kubernetes files.
- Dockerfiles and Structurizr DSL files (`.dsl`).
- ADRs: Markdown files in `docs/adr`, `doc/adr` or `docs/decisions`.

The browser leaves out:

- Every path that a `.gitignore` file ignores. The root file, nested files and `.git/info/exclude` apply, as git reads them.
- The folders `node_modules`, `vendor`, `bin`, `obj`, `dist` and `.git`.
- Files that look like secrets, such as `.env`, `*.pem`, `id_rsa` and `credentials.json`.
- Lock files, minified files and source maps.

The browser packs the files as a `.zip` and uploads it to an archive Source with the name of the folder. The progress line shows the packing, then the upload. The server checks the archive as it checks every upload ([Upload an archive](/how-to/upload-an-archive/)). Then a scan of the archive starts and the **Import** panel opens.

When the files to send are larger than the workspace limit `import.maxArchiveBytes`, the browser refuses the folder and sends nothing.

### Read the folder again

The browser holds no lasting access to the folder. To read a changed folder, choose it again:

1. Open **Import**.
2. Find the archive Source of the folder.
3. Select **Choose folder again** and select the same folder.

Each read is a new Snapshot of the same Source. **Choose folder** on an empty project also uses the archive Source with the name of the folder when the project has one.

## Read a GitHub repository

1. In the field below **Choose folder**, type the repository, such as `acme/orders` or `https://github.com/acme/orders`.
2. Select **Read**.

Vellum creates a `github` Source and starts its first scan. The repository needs a GitHub connection: [Connect GitHub](/how-to/connect-github/). If the project has a Source for the repository, **Read** scans it again.

## Describe it

1. Type a description of the system in the text box, for example `A shop: customers order on a web app, the Orders API stores orders in Postgres.`
2. Select **Describe it**.

Vellum sends the text to the AI provider of the workspace. The proposal opens. You accept or reject each item. Nothing goes into the model before you accept it. Without an AI provider, the card says so. Use **Type it** instead.

## Type it

**Type it** writes facts that you type, with no AI provider.

1. Select **Type it**. The box **Type it** opens at the bottom of the canvas.
2. Type one fact on each line.
3. Press Enter. Shift+Enter starts a new line, so you can send several lines with one Enter.

Each line that Vellum can read becomes `declared` elements and relationships in the model. Vellum then lays out the model with the auto layout. A line that Vellum cannot read stays in the box, with the reason below it. The box stays open until you close it.

### The lines

| Line | Result |
|---|---|
| `Orders API` | A system with the name `Orders API`. |
| `Customer [actor]` | An actor. |
| `Orders DB [store] in Shop` | A store inside the system `Shop`. |
| `Web app -> Orders API` | A relationship from `Web app` to `Orders API`. |
| `Web app -> Orders API: places orders` | The same relationship with the label `places orders`. |
| `# a note` | Nothing. A line that starts with `#` is a comment. |

The kind in brackets is one of `actor`, `system`, `app`, `store` or `component`. These words also work: `person` and `user` for an actor, `service`, `api`, `web` and `worker` for an app, `db`, `database`, `queue`, `topic`, `cache` and `bucket` for a store.

Each end of a relationship can have a kind and `in`, as an element line can: `Orders API [app] in Shop -> Orders DB [store] in Shop: reads and writes`.

`in` is a parent only after a kind in brackets. `Log in service` is one name.

### Names and kinds

- A name that the model has already is that element. Case does not matter. Vellum does not change its kind or its parent.
- A name with no kind becomes a system.
- An actor and a system are at the top level.
- An app and a store go inside a system. Without `in`, they go inside the system with the name of the project. Vellum creates that system when the model does not have it.
- A component goes inside an app, so it needs `in`. Vellum creates an app that does not exist inside the system with the name of the project.
- Vellum refuses a line that breaks these rules, for example a store inside an actor.
- Vellum writes no second relationship with the same ends and the same label.

## Open the sample

Select **Open the sample**. Vellum imports the Orders example (`examples/orders` in the repository): 8 elements, 5 relationships, the contracts and the two scenarios **Place an order** and **Place an order when Inventory is down**. Every fact has the truth label `declared`. Vellum then lays out the model.

The sample is a copy in this project. A change to it changes nothing else.

## Next

- [Model a system](/how-to/model-a-system/)
- [Import a repository](/how-to/import-a-repository/)
- [Trace a scenario](/how-to/trace-a-scenario/)
