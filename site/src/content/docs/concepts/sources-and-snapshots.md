---
title: Sources, Snapshots and bindings
description: Where imported evidence comes from, how Vellum identifies what it read, and how it links an element to its counterpart in each Source.
---

This page explains the three words of an import: Source, Snapshot and source binding.

## A Source is one origin of evidence

A project has Sources. A Source is one place that Vellum reads evidence from, such as one GitHub repository. A Source has a kind, a name and its connection details.

The kinds are `github`, `gitlab`, `bitbucket`, `azure-devops`, `directory`, `archive`, `agent-push`, `model-file`, `traces` and `newrelic`. This version of Vellum reads `github`, `gitlab`, `bitbucket`, `azure-devops`, `directory`, `archive`, `traces` and `newrelic`, and takes `agent-push`. It refuses to create a Source of another kind with the code `source_kind_unavailable`.

A `traces` Source and a `newrelic` Source are runtime Sources. Their Snapshot has one file: a document that Vellum derives, with one line for each service and each edge. Their Snapshot ID is the SHA-256 of those sorted lines, and a citation names the line. See [Runtime sources](/reference/runtime-sources/).

An `agent-push` Source holds what `vellum scan` pushed from a machine that has the code: a manifest, facts and citations, and no file content. Its first push creates it. Vellum cannot scan it again, because Vellum does not hold its files. See [Push evidence from CI or a laptop](/how-to/push-evidence-from-ci-or-a-laptop/).

| Kind | What Vellum reads | Revision | Profile |
|---|---|---|---|
| `github` | One repository at one commit, through the GitHub REST API. See [Import a repository](/how-to/import-a-repository/). | The commit SHA | Both |
| `gitlab`, `bitbucket`, `azure-devops` | One repository at one commit, through the REST API of the host. See [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/). | The commit SHA | Both |
| `directory` | One folder under the sources root of the instance, as it is at the time of the scan. See [Import a local folder](/how-to/import-a-local-folder/). | The Snapshot ID | Local only |
| `archive` | One uploaded `.zip` or `.tar.gz` file. See [Upload an archive](/how-to/upload-an-archive/). | The Snapshot ID | Both |

`GET /api/projects/{projectId}/sources/kinds` lists the kinds that a new Source of the project can have, with the reason when a kind cannot be used now. A git host has no usable API base URL, or the instance is hosted, or it has no sources root.

The first scan of a GitHub repository creates its Source. **Add Source** and **Add many repositories** under **Sources** create a Source of any kind. A project has one Source for each place: one repository, one folder or one archive name. The import screen lists the Sources under **Sources**.

### Delete a Source

Select **Delete** on the Source, then **Delete Source**. `DELETE /api/projects/{projectId}/sources/{sourceId}` does the same. You need the editor role, signed in as a person. Vellum deletes the scans, the Snapshots, the proposals and the fact links of the Source, and each stored file that no other Snapshot uses. A scan of the Source that runs stops.

The model keeps what people accepted. A deleted Source takes no element and no fact out of the model.

Each Source keeps its own facts. When you accept a proposal of a Source, Vellum records which Source the fact came from. A scan of one Source never changes the facts of another Source.

## A Snapshot is what one scan read

Each completed scan is a Snapshot of its Source. A Snapshot does not change.

A Snapshot is a manifest. The manifest lists each file that the scan read, with its path, its size in bytes and its git blob SHA. Vellum computes the git blob SHA from the content: the SHA-1 of `blob <size>`, a NUL byte and the bytes. It is the value that `git hash-object` gives.

The Snapshot ID is the SHA-256 of the manifest. Vellum sorts the entries by the UTF-8 bytes of their paths and writes each entry as path, NUL, size, NUL, SHA and a line feed. The same files give the same Snapshot ID, from any scan and from any kind of Source.

So one tree gives one Snapshot ID when Vellum reads it as a git commit, as an archive and as a folder:

- A folder scan does not list a `.git` folder.
- A folder scan does not follow a symbolic link. It reads the link as git stores it: a file whose content is the text of its target.
- An archive scan removes one top folder when every file is in it. For example, `orders-main/src/Program.cs` becomes `src/Program.cs`. Vellum removes one folder only. If every file of the tree is in one folder, such as `src/`, and the archive has no top folder, Vellum removes `src/` and the ID differs. Pack the files in one top folder to prevent this.
- A git scan, a folder scan and an archive scan apply the same [limits and exclusions](/how-to/import-a-repository/#limits). A scan with other limits can read other files and give another ID.

An archive cannot hold a symbolic link. Vellum refuses such an archive. So a tree with a link gives the same ID as a git commit only when you read it as a folder.

A git Source also records the commit SHA of each Snapshot. A folder that has a `.git` folder also shows the commit SHA that its `HEAD` names. Vellum reads that SHA as text and does not check it against the files. It is information only. The Snapshot ID is the identity of a folder scan.

## A citation names the Source and the revision

A fact that an import wrote cites its evidence as `<source>@<revision>:path:start-end`. For a git Source, the revision is the commit SHA:

```text
github.com/acme/orders@38035b90c4a1e3f2d8b7a6c5d4e3f2a1b0c9d8e7:orders-api/Program.cs:40-50
```

For a Source that is not git, the revision is the Snapshot ID:

```text
agent-push/orders-api@def2307d05b4ab8e76e7ce3788825385a4e1ec1bf6464524612f5ce232f3fc7e:orders-api/Program.cs:40-50
```

| Kind | Citation |
|---|---|
| `github` | `github.com/<owner>/<name>@<commit>:path:start-end` |
| `gitlab` | `gitlab.com/<group>/<name>@<commit>:path:start-end`, or the host of a self-managed instance |
| `bitbucket` | `bitbucket.org/<workspace>/<repository>@<commit>:path:start-end` |
| `azure-devops` | `dev.azure.com/<organization>/<project>/<repository>@<commit>:path:start-end` |
| `directory` | `directory/<path>@<snapshot-id>:path:start-end`. `<path>` is the folder under the sources root. |
| `archive` | `archive/<name>@<snapshot-id>:path:start-end`. `<name>` is the name of the Source in lower case, with a dash in place of each other character. |

## Staleness compares two Snapshots of one Source

When a scan of a Source looks at the files of an accepted fact and no longer finds the fact, Vellum marks the fact stale. It compares only Snapshots of the same Source. A scan of another Source says nothing about the fact.

Vellum does not mark a fact stale while another Source still holds it. Only a scan of the Source that set a mark can clear it.

To see what changed between two Snapshots of one Source, use `GET /api/projects/{projectId}/sources/{sourceId}/snapshots/compare?base={scanId}&head={scanId}`. The answer lists the files that were added, removed or changed, and the facts that were added, removed or changed.

## A source binding links an element to a Source

An element holds source bindings. A source binding is a pair of a Source and an external key: the key that names the element in that Source, such as `service:orders-api`.

Vellum adds a binding when a person accepts an element fact of a Source, or answers **Same** to a "same as?" question. The binding is a model event, so it is part of the history of the model.

When a scan finds a key that is bound, the proposal targets the bound element.

## Vellum never binds by name

Two Sources can describe the same service. A scan of the second Source finds an element fact whose name is the name of an element that the first Source holds. Vellum does not join them. It asks the question "same as?". It asks the same question when the element with that name was made by a person:

- **Same** binds the element to the key of the second Source. The values of the second Source then come back as a proposal for that element.
- **Not the same** makes the fact a new element. Vellum does not ask again for this key and this element.

Until you answer, Vellum proposes nothing for the fact and for the facts that depend on it.

## Conflicts between Sources

When a scan finds an entity that another Source holds, such as a channel or a message with the same name, the proposal names the other Source. When the values differ, the proposal is a conflict. The review shows the value in the model, the value of this scan and the value of each other Source, with the truth label and the citation of each. No Source wins by itself.

When a scan of one Source no longer finds an entity that another Source still holds, the removal it proposes removes the fact of that Source only. The entity stays in the model.

A scan applies its additions and the changes of facts that only its Source wrote. It never applies a conflict, a question, a proposal about an entity that another Source holds, or a removal. These wait in **Needs you** for a person.

When you accept a proposal, Vellum plans the pending proposals of every other Source again. An addition of another Source whose element you just accepted becomes a "same as?" question. Vellum also refuses an addition when an element of that kind and name came into the model after the scan was planned, and asks the question instead.

## Dependencies that another Source does not see

A model file can declare a dependency, for example `Orders → Orders DB` in a Backstage catalog. When another Source observes or measures the element the dependency starts at, and has no such edge from it, the **Sources** panel lists the dependency for both Sources, under **Dependencies another Source does not see**. The list shows the declaration with its citation and the Source that does not see it. It does not stop you from accepting the dependency.

## See also

- [Import a repository](/how-to/import-a-repository/)
- [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/)
- [Import a local folder](/how-to/import-a-local-folder/)
- [Upload an archive](/how-to/upload-an-archive/)
- [Add many repositories](/how-to/add-many-repositories/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
- [Extraction rules](/reference/extraction-rules/)
