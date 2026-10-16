---
title: Diagram export formats
description: The SVG, PNG, Mermaid and Structurizr DSL exports of views, scenarios and the model, with the endpoints, the mapping of each format, what each drops and how text is escaped.
---

Vellum exports a view as SVG, PNG or Mermaid, a scenario as an SVG or Mermaid sequence diagram, and the model as Structurizr DSL. Vellum does not import SVG, PNG or Mermaid. A Structurizr DSL file reads back as the same elements and relationships: see [Model file mappings](/reference/model-file-mappings/). [Export a diagram](/how-to/export-a-diagram/) shows the steps.

## Endpoints

| Endpoint | Role | Answer |
|---|---|---|
| `GET /api/projects/{projectId}/views/{viewId}/export/svg` | Viewer | `image/svg+xml`, UTF-8 |
| `GET /api/projects/{projectId}/views/{viewId}/export/mermaid` | Viewer | `text/plain`, UTF-8 |
| `GET /api/projects/{projectId}/scenarios/{scenarioId}/export/svg` | Viewer | `image/svg+xml`, UTF-8 |
| `GET /api/projects/{projectId}/scenarios/{scenarioId}/export/mermaid` | Viewer | `text/plain`, UTF-8 |
| `GET /api/projects/{projectId}/export/structurizr` | Viewer | `text/plain`, UTF-8 |

Each endpoint takes the optional query parameter `revisionId`. With it, the export reads that named revision. Without it, the export reads the main model at its version now. An agent token with the `read` scope can call each endpoint.

The scenario endpoints also take the optional query parameter `branchId`, the stream ID of an open draft or variant. The export then takes the names from that draft. Give `revisionId` or `branchId`, not both.

| Answer | When |
|---|---|
| `400` | `revisionId` names no revision of the project, `branchId` names no draft or variant of the project, or the request gives `revisionId` and `branchId`. |
| `404` | The view or the scenario is not in the project, or the draft or variant of `branchId` is not open. |

PNG has no endpoint. The browser draws the SVG on a canvas at twice its size and saves the PNG. A side of the PNG is at most 8192 pixels.

## What a view shows

A view shows one level of the model.

- A view without a root element shows the top-level elements.
- A view with a root element shows the children of that element, inside a frame that has the name, the kind and the technology of the root.
- A relationship shows when both of its ends show.

The order of the elements is the ordinal order of the names, then of the IDs.

## Positions

An element with a saved position in the layout of the view is at that position. The other elements are on a grid of three columns. The columns are 340 apart and the rows are 200 apart.

- When no element of the level has a saved position, the grid starts at 0,0. This is the grid that the canvas shows for a view that nobody arranged.
- When some elements have a saved position, the grid starts at the left edge of those elements and 80 below the lowest of them. An element without a position does not cover an element with a position.

The layout of a view is not part of a revision. An export of a revision uses the layout of the view now.

## Layout fingerprint

Each export names the layout it was drawn from as a layout fingerprint: `sha256:` and the SHA-256 of these parts, in this order:

1. the view name;
2. the root element ID;
3. each saved position (element ID, x, y), in the order of the element IDs;
4. each relationship with a saved side (`side`, relationship ID, source side, target side), in the order of the relationship IDs. An auto end is an empty word.

A view with no saved side has no part 4. Its fingerprint is the same as before Vellum saved sides.

The SVG has the fingerprint in `<desc>`. The Mermaid file has it in the second comment line. The Structurizr DSL file has one comment line for each view.

The same revision and the same layout fingerprint give the same bytes. When somebody renames the view, changes its root, moves an element or changes the side of a relationship, the fingerprint changes.

## SVG

The SVG looks like the canvas in the dark theme: the cards, the frame of the root, the relationships and their labels.

| On the canvas | In the SVG |
|---|---|
| Card | A card with the kind colour, the icon, the name, the kind, the technology and two lines of the description |
| Truth label | A dot for a label other than `declared`. The dot has a title with the label. |
| Stale mark | A dashed `STALE` badge |
| Elements inside | A badge with the count |
| Relationship | A path with an arrow. A behavioural relationship is teal and thicker. The line shape (curve, straight or step) is kept. Each end attaches to its saved side, or to the side that faces the other card when the side is auto, as on the canvas. |
| Relationship label | A label card with the label and the technology |

The same revision and the same layout fingerprint give the same bytes:

- the lists have a fixed order;
- numbers have at most two decimals and use a full stop;
- the file has no time, no random ID and no user name;
- the fonts are named by family (`Space Grotesk`, `Inter`, `JetBrains Mono`) and are not in the file.

The `<desc>` element of the file names the revision, or the stream and its version, and the layout fingerprint, and lists what the picture drops: the provenance of truth labels, element status, descriptions beyond two lines, tags, owners, elements on other levels, messages, contracts, schemas, scenarios, promises, costs and comments.

Text goes into the file as XML text. The characters `&`, `<`, `>`, `"` and `'` become entities. A character that XML 1.0 does not allow becomes U+FFFD. A line break in a name becomes a space. A name such as `<script>` shows as text and is never markup.

## Mermaid

A Mermaid export is a flowchart from top to bottom.

| Vellum | Mermaid |
|---|---|
| Root element of the view | `subgraph` around the elements |
| Actor | Stadium `id(["..."])` |
| Store | Cylinder `id[("...")]` |
| Component | Subroutine `id[["..."]]` |
| System, app | Rectangle `id["..."]` |
| Structural relationship | `a -->\|"label [technology]"\| b` |
| Behavioural relationship | `a ==>\|"label [technology]"\| b` |

A node label is the name, a line break (`<br/>`), then the kind and the technology.

The first three lines are comments. They name the view, the revision and the layout fingerprint, and list what the export drops: layout and positions, icons, truth labels and provenance, element status, descriptions, tags, owners, line shapes, elements on other levels, messages, contracts, schemas, scenarios, promises, costs and comments.

A label is always in double quotes. In a label, each of these characters becomes a Mermaid entity code, such as `#quot;` or `#lt;`: `#`, `"`, `&`, `<`, `>`, `|`, the backtick, `[`, `]`, `(`, `)`, `{`, `}`, `;`, `%` and `\`. A name cannot close the label, add markup or start a directive. In a comment line, a space follows each `%`, so a name cannot form `%%{`.

## Scenario sequence diagram

A scenario exports as a sequence diagram. The Mermaid file and the SVG come from the same sequence, so they show the same participants, arrows, blocks and notes.

A scenario is not part of a revision. The export reads the steps of the scenario now. It reads the names of elements, operations, channels, messages and subscriptions from the model that `revisionId` or `branchId` selects. The header names the scenario, the version of the scenario and that model. The same scenario version and the same model give the same bytes.

| Scenario | Mermaid | SVG |
|---|---|---|
| Element that a step names | `participant id as Name`, in the order of first use | A box and a dashed lifeline |
| Actor | `actor id as Name` | A box with round ends |
| Channel that a step names or that a message reaches | `participant id as name (kind)` | A teal box with the kind |
| `call` | `caller->>owner: operation`, the `then` steps, then `owner-->>caller: response` | A solid arrow with a closed head, then a dashed arrow back |
| `onError` of a call | `alt <operation> fails`, with the injected faults on the operation or its owner, around the `onError` steps | A frame with the tab `ALT` |
| `publish` | `publisher-)channel: message` | A teal arrow with an open head |
| Binding that passes the message | `source-)target: message` | A teal arrow |
| Delivery to a subscription | `channel-)consumer: message`, then the handler steps of the subscription | A teal arrow, then the handler steps |
| Two or more deliveries of one message | `par` with one section for each subscription | A frame with the tab `PAR` |
| Handler of a subscription that the message does not reach | `Note over publisher` | A note |
| `write` | `writer->>store: dataset` | A solid arrow with the dataset as its label |
| `parallel` | `par branch 1`, `and branch 2` … An empty branch is not shown. | A frame with the tab `PAR` |
| `branch` | `alt if <step> succeeded` and `else otherwise`. With an empty `then`: `alt if <step> did not succeed`. | A frame with the tab `ALT` |
| Fault | `Note over` the target, at the start | A note |
| Assertion | `Note over` all participants, at the end | A note |

A message follows the routes of the trace engine. The subscriptions on a channel come first, in the order of their IDs. Then each binding of the channel, in the order of its ID, passes the message when its routing key matches the routing key of the step. A channel takes a message once.

The export shows the declared flow, not a playback. A fault does not close a route in the export. To see the outcome of each step, play the scenario.

A step that names an element, an operation, a channel, a message or a subscription that the model does not hold shows `unknown` and the first eight hexadecimal digits of the ID. A call to an unknown operation is a note over the caller.

The first three lines of the Mermaid file are comments. The SVG has the same text in `<desc>`. They name the scenario, its version and the model, and list what the export drops: payloads, headers, routing keys, schemas, transports, timeouts, retry policies, dead-letter routes, the result of a playback (outcomes, attempts, timing and the routes that a fault closes), truth labels and their provenance, promises, costs and comments.

Participant IDs come from the names, as in [Identifiers](#identifiers). The sequence diagram reserves other words, such as `alt`, `par`, `note` and `participant`. Each text after `as`, after a colon or after `alt`, `else`, `par` and `and` uses the same entity codes as a Mermaid label, so a name cannot end the line, start a block or add markup. The SVG escapes text as the view SVG does.

The bundle of an SDD holds each scenario as `scenarios/<scenario ID>.json`. It does not hold the sequence diagrams, and it does not hold the view diagrams.

## Structurizr DSL

The Structurizr DSL export is a workspace with the whole model and the views.

| Vellum | Structurizr |
|---|---|
| Top-level actor | `person` |
| Other top-level element | `softwareSystem` |
| Element inside a software system | `container` |
| Element inside a container | `component` |
| Store | The tag `Database`, drawn as a cylinder |
| Relationship | `a -> b "label" "technology"` |
| Behavioural relationship | The tag `Behavioural` |
| View without a root element | `systemLandscape` |
| View whose root is a software system | `container` view |
| View whose root is a container | `component` view |

Each view has `include *` and `autoLayout`. A view with another root is named in a comment and not written.

The `model` block starts with `!impliedRelationships false`. Structurizr then shows only the relationships that Vellum holds.

Structurizr needs a unique name among the people and software systems, among the containers of one software system and among the components of one container. Vellum does not. A later element with a name that is already used gets ` (2)`, then ` (3)`, in the order of the file. A header comment names each element that the export renamed.

Structurizr cannot nest an actor, or an element inside a person or a component. Such an element moves to the top level with the elements inside it. A relationship between an element and its parent, and a second relationship with the same label between the same two elements, are not allowed in Structurizr. Each is named in a comment and not written.

The first lines are comments. They name the project, the revision and the layout fingerprint of each view, and list what the export drops: layout and positions, icons, line shapes, truth labels and provenance, element status, owners, stale marks, messages, contracts, schemas, scenarios, promises, obligations, decisions, documents, costs, comments, the technology of a person or a software system, and the nesting that Structurizr cannot express.

A string is in double quotes on one line. Structurizr knows one escape, `\"`:

- A double quote gets a backslash in front.
- A backslash stays as it is. A backslash at the end of a string becomes U+29F5 (⧵), so that it cannot escape the closing quote.
- Structurizr replaces `${NAME}` with a constant or an environment variable. Each `${` gets U+200B (zero width space) between `$` and `{`, so a name such as `${HOME}` stays text.
- A line break becomes a space. A comma in a tag becomes a space.

## Identifiers

Mermaid node IDs and Structurizr identifiers come from the names:

1. The name in lower case. Each run of characters other than `a`–`z` and `0`–`9` becomes one underscore.
2. A name that gives nothing becomes `element`. A name that starts with a digit gets `element_` in front.
3. A word that the format reserves, such as `end` in Mermaid or `model` in Structurizr, gets an underscore at the end.
4. A second use of an identifier gets `_2`, then `_3`, in the ordinal order of the names, then of the IDs.

The same names give the same identifiers. A rename can change an identifier.
