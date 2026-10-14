---
title: Sources, Snapshots and bindings
description: Where imported evidence comes from, how Vellum identifies what it read, and how it links an element to its counterpart in each Source.
---

This page explains the three words of an import: Source, Snapshot and source binding.

## A Source is one origin of evidence

A project has Sources. A Source is one place that Vellum reads evidence from, such as one GitHub repository. A Source has a kind, a name and its connection details.

The kinds are `github`, `gitlab`, `bitbucket`, `azure-devops`, `directory`, `archive`, `agent-push`, `model-file`, `traces` and `newrelic`. This version of Vellum reads `github` and takes `agent-push`. It refuses to create a Source of another kind with the code `source_kind_unavailable`.

An `agent-push` Source holds what `vellum scan` pushed from a machine that has the code: a manifest, facts and citations, and no file content. Its first push creates it. Vellum cannot scan it again, because Vellum does not hold its files. See [Push evidence from CI or a laptop](/how-to/push-evidence-from-ci-or-a-laptop/).

The first scan of a repository creates its Source. A project has one Source for each repository. The import screen lists the Sources under **Sources**.

Each Source keeps its own facts. When you accept a proposal of a Source, Vellum records which Source the fact came from. A scan of one Source never changes the facts of another Source.

## A Snapshot is what one scan read

Each completed scan is a Snapshot of its Source. A Snapshot does not change.

A Snapshot is a manifest. The manifest lists each file that the scan read, with its path, its size in bytes and its git blob SHA. Vellum computes the git blob SHA from the content: the SHA-1 of `blob <size>`, a NUL byte and the bytes. It is the value that `git hash-object` gives.

The Snapshot ID is the SHA-256 of the manifest. Vellum sorts the entries by the UTF-8 bytes of their paths and writes each entry as path, NUL, size, NUL, SHA and a line feed. The same files give the same Snapshot ID, from any scan and from any kind of Source.

A git Source also records the commit SHA of each Snapshot.

## A citation names the Source and the revision

A fact that an import wrote cites its evidence as `<source>@<revision>:path:start-end`. For a git Source, the revision is the commit SHA:

```text
github.com/acme/orders@38035b90c4a1e3f2d8b7a6c5d4e3f2a1b0c9d8e7:orders-api/Program.cs:40-50
```

For a Source that is not git, the revision is the Snapshot ID:

```text
agent-push/orders-api@def2307d05b4ab8e76e7ce3788825385a4e1ec1bf6464524612f5ce232f3fc7e:orders-api/Program.cs:40-50
```

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

When a scan finds an entity that another Source holds, such as a channel or a message with the same name, the proposal is a conflict, also when the values agree. The review shows the value in the model, the value of this scan and the value of each other Source, with the truth label and the citation of each. No Source wins by itself.

When a scan of one Source no longer finds an entity that another Source still holds, the removal it proposes removes the fact of that Source only. The entity stays in the model.

**Accept all from this Source** never accepts a conflict, a question, a proposal that another Source holds, or a change of a truth label.

## See also

- [Import a repository](/how-to/import-a-repository/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
- [Extraction rules](/reference/extraction-rules/)
