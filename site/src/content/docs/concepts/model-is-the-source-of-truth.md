---
title: The model is the source of truth
description: Why Vellum keeps one event-sourced model, and why the canvas, documents, imports, AI output and exports are projections of it or proposals to it.
---

This page explains where the truth of a design is in Vellum, and what each other thing on the screen is.

## One model

A project has one model. The model holds the elements, the relationships, the messages and the contracts: operations, channels, subscriptions and bindings. Each of these is a fact with a truth label. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

Everything else that you see is one of two things.

| Thing | What it is |
|---|---|
| The canvas | A projection. It draws the model. |
| A project document, the SDD, the bundle, a model export | A projection. It is generated from the model at one version. |
| A trace, a review, a cost estimate, a stress result | A result that is computed from the model at one version. |
| A repository import | A proposal. It states what a scan found. |
| An answer of the assistant | A proposal. It states what the assistant suggests. |
| A write of a coding agent | A proposal or a draft. |

A projection cannot be wrong in a way that the model is right: Vellum derives it again from the model. A proposal changes nothing until a person accepts it.

## The model is a list of events

Vellum does not store the model as a diagram or as one document that is overwritten. It stores the list of changes: an element was added, an element was renamed, a relationship was removed, a provenance was recorded. The model at a moment is the result of these events in order.

This has three effects.

- **History is kept.** No change overwrites an earlier one. The audit of who changed what is the data itself.
- **A version is exact.** The number of events in the stream is the version of the model. A result, an export and a pinned bundle name the version that they read. The same version gives the same result. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#determinism-rules).
- **Other views can be built again.** The tables that the canvas and the search read are derived from the events. If such a table is lost or its shape changes, Vellum builds it again from the events.

A revision is a name for the model at one version. A draft and a variant are streams of their own that start from the main model at one version. See [Compare variants](/how-to/compare-variants/) and [Work in a draft](/how-to/work-in-a-draft/).

## Stable IDs

Each fact has an ID that does not change for the life of the fact. A rename changes the name, not the ID.

Everything outside the model refers to a fact by its ID: a scenario step, a promise link, a finding, a blocker, a cost resource, a line of the SDD, a commit message of a coding agent. So a rename does not break a scenario, and a requirement ID in a test still points at the same promise after the design changes.

A model export keeps the IDs, and an import matches by ID. See [Model document format](/reference/model-document-format/#id-rules).

## Two writers do not overwrite each other

A change is decided against the model at one version and is appended only if the model is still at that version. When two changes arrive at the same time, one is appended. The other gets `409` with the type `conflict`, and writes nothing. The person reloads and makes the change again on the new model.

Vellum does not merge two changes silently, and the last writer does not win. Settings use the same idea with an explicit version: see [Settings](/reference/settings/#concurrency). [Conflicts](/troubleshooting/conflicts/) lists each conflict and its fix.

## The canvas is a projection

The canvas shows a part of the model at one level. A drag, a connection or a delete on the canvas sends a change to the model. The canvas then draws the model again.

Where a node is on the canvas is layout. Vellum stores layout in a view, apart from the model: the position and the size of each node, and the shape of each line. Layout has no meaning.

- Two nodes that are near each other are not related.
- A node that is inside the frame of another on the screen is its child only because the model says so.
- A change of a position is not a change of the model. It makes no event in the model stream, moves no version and makes no result stale.

A model export holds no view and no position. One project can have many views of one model.

## A relationship is structure, a scenario is behaviour

A line on a diagram is easy to read as "A calls B, then B answers". Vellum does not read it so.

A relationship says that two elements are connected. It has no order, no payload, no timeout and no failure. By default a relationship is `structural`: it has no executable meaning.

Behaviour is in a scenario. A scenario is an ordered list of steps over declared contracts: this element calls this operation, this element publishes this message to this channel. A trace replays a scenario, never a diagram. See [A trace is declared behaviour](/concepts/traces-are-declared/) and [Scenario format](/reference/scenario-format/).

This separation keeps a diagram honest. A line that somebody drew for orientation does not become a claim about run-time behaviour.

## Proposals never write silently

Three sources produce content that is not from a person: a repository scan, an AI provider and a coding agent. Text from a repository and output of a language model are untrusted input. They are never instructions, and they are never facts by themselves.

So each source writes a proposal.

- A scan proposes facts with the label `observed-in-source`. A person accepts or rejects each. See [Import a repository](/how-to/import-a-repository/).
- The assistant proposes additions and flows. A fact from an accepted suggestion has the label `inferred`, with a source and a confidence. See [Design with the assistant](/how-to/design-with-the-assistant/).
- An agent token can propose. It cannot change the model. See [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

When a person accepts a proposal, the model records who accepted it and when. A label never rises by itself: an inferred fact stays inferred until a person changes it.

A scan never overwrites what a person set. A difference between the scan and the model becomes a conflict that shows both values.

## One exception to know

`POST /api/projects/{projectId}/import` reads a model document and writes it into the main model directly. It makes no proposal. It is a tool for a person with the editor role who moves a model between projects, and an agent token cannot call it. The document carries its own truth labels, and the import keeps them. See [Model document format](/reference/model-document-format/#import-behaviour).

## What follows from this

- To change the design, change the model. An edit to a generated document does not change the design, and the next export does not hold the edit.
- To know if a result is current, compare the version that it names with the version of the model now.
- To trust a fact, read its label and its provenance, not its place on a diagram.
