---
title: Diagram export formats
description: The SVG, PNG, Mermaid and Structurizr DSL exports, with the endpoints, the mapping of each format, what each drops and how text is escaped.
---

Vellum exports a view as SVG, PNG or Mermaid, and the model as Structurizr DSL. Each export is one-way: Vellum does not import these files. [Export a diagram](/how-to/export-a-diagram/) shows the steps.

## Endpoints

| Endpoint | Role | Answer |
|---|---|---|
| `GET /api/projects/{projectId}/views/{viewId}/export/svg` | Viewer | `image/svg+xml`, UTF-8 |
| `GET /api/projects/{projectId}/views/{viewId}/export/mermaid` | Viewer | `text/plain`, UTF-8 |
| `GET /api/projects/{projectId}/export/structurizr` | Viewer | `text/plain`, UTF-8 |

Each endpoint takes the optional query parameter `revisionId`. With it, the export reads that named revision. Without it, the export reads the main model at its version now. An agent token with the `read` scope can call each endpoint.

| Answer | When |
|---|---|
| `400` | `revisionId` names no revision of the project. |
| `404` | The view is not in the project. |

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

## SVG

The SVG looks like the canvas in the dark theme: the cards, the frame of the root, the relationships and their labels.

| On the canvas | In the SVG |
|---|---|
| Card | A card with the kind colour, the icon, the name, the kind, the technology and two lines of the description |
| Truth label | A dot for a label other than `declared`. The dot has a title with the label. |
| Stale mark | A dashed `STALE` badge |
| Elements inside | A badge with the count |
| Relationship | A path with an arrow. A behavioural relationship is teal and thicker. The line shape (curve, straight or step) is kept. |
| Relationship label | A label card with the label and the technology |

The same model and the same layout give the same bytes:

- the lists have a fixed order;
- numbers have at most two decimals and use a full stop;
- the file has no time, no random ID and no user name;
- the fonts are named by family (`Space Grotesk`, `Inter`, `JetBrains Mono`) and are not in the file.

The `<desc>` element of the file names the revision, or the stream and its version, and lists what the picture drops: the provenance of truth labels, element status, descriptions beyond two lines, tags, owners, elements on other levels, messages, contracts, schemas, scenarios, promises, costs and comments.

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

The first three lines are comments. They name the view and the revision, and list what the export drops: layout and positions, icons, truth labels and provenance, element status, descriptions, tags, owners, line shapes, elements on other levels, messages, contracts, schemas, scenarios, promises, costs and comments.

A label is always in double quotes. In a label, each of these characters becomes a Mermaid entity code, such as `#quot;` or `#lt;`: `#`, `"`, `&`, `<`, `>`, `|`, the backtick, `[`, `]`, `(`, `)`, `{`, `}`, `;`, `%` and `\`. A name cannot close the label, add markup or start a directive. In a comment line, a space follows each `%`, so a name cannot form `%%{`.

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

Structurizr cannot nest an actor, or an element inside a person or a component. Such an element moves to the top level with the elements inside it. A relationship between an element and its parent, and a second relationship with the same label between the same two elements, are not allowed in Structurizr. Each is named in a comment and not written.

The first three lines are comments. They name the project and the revision, and list what the export drops: layout and positions, icons, line shapes, truth labels and provenance, element status, owners, stale marks, messages, contracts, schemas, scenarios, promises, obligations, decisions, documents, costs, comments, the technology of a person or a software system, and the nesting that Structurizr cannot express.

A string is in double quotes on one line. A backslash and a double quote get a backslash in front. A line break becomes a space. A comma in a tag becomes a space.

## Identifiers

Mermaid node IDs and Structurizr identifiers come from the names:

1. The name in lower case. Each run of characters other than `a`–`z` and `0`–`9` becomes one underscore.
2. A name that gives nothing becomes `element`. A name that starts with a digit gets `element_` in front.
3. A word that the format reserves, such as `end` in Mermaid or `model` in Structurizr, gets an underscore at the end.
4. A second use of an identifier gets `_2`, then `_3`, in the ordinal order of the names, then of the IDs.

The same names give the same identifiers. A rename can change an identifier.
