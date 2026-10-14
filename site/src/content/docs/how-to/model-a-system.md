---
title: Model a system
description: Create a project, add elements and relationships on the canvas, save diagrams and attach messages and JSON schemas.
---

This guide shows you how to model a system: the elements, the relationships between them, the diagrams that show them and the messages that they exchange. You need the editor role in the workspace.

The model is the source of truth. The canvas shows the model. A change on the canvas writes to the model.

## Create a project

1. Open `http://localhost:8080` and sign in.
2. Below **Workspaces**, find the workspace. If there is none, select **Create Workspace**, enter a **Workspace name** and select **Create**.
3. Select the workspace, then select **+ New Project**.
4. In **Create Project**, enter a **Project name** and select **Create**.
5. Open the project.

Vellum creates a diagram named `Default` the first time you open a project that has no diagram.

## Know the element kinds

A model has five kinds of element. Each kind has one permitted parent.

| Kind | Parent | Shortcut |
|---|---|---|
| Actor | None. An actor is at the top level. | `Shift+P` |
| System | None. A system is at the top level. | `Shift+S` |
| App | A system. | `Shift+A` |
| Store | A system. | `Shift+D` |
| Component | An app. | `Shift+X` |

The server refuses an element with the wrong parent. The answer is `400` with a message such as `App requires a parent of kind System`.

## Add an element

The canvas shows one level at a time. The top level is **Context**. A new element gets the level that you look at as its parent. Vellum offers only the kinds that the level permits.

Use one of these methods.

- Select **Element catalog** in the left toolbar. Below **Elements**, select a kind.
- Press the shortcut of the kind.
- Double-click an empty part of the canvas. Select a kind.
- Right-click an empty part of the canvas. Select **Add Actor** or **Add System** (at the top level), or the kinds that the level permits.
- Press `N`. In **Add Element**, select the kind, enter an **Element name** and select **Add**.
- Open the command palette with `Ctrl+K` or `Cmd+K`. Select **Add element**.
- Drag from the handle of an element to an empty part of the canvas. Select a kind. Vellum adds the element and a relationship from the first element to it.

Except for the **Add Element** dialog, a new element has the name `New <kind>`. Rename it in the detail panel.

## Go into a system or an app

1. Double-click an element that has children. As an alternative, right-click it and select **Drill into**.
2. Add the elements of that level.
3. Select **Context** or a name in the breadcrumb to go up.

**Expand** in the same menu shows the children in place. **Collapse** hides them again.

To add the first child of an empty system or app, right-click it and select **Add app inside**, **Add store inside** or **Add component inside**. As an alternative, select it and use the buttons below **Inside this system** in the detail panel. **Drill into** and the double-click work only for an element that already has a child.

The import from GitHub and the assistant also add children when you accept their proposals. See [Import a repository](/how-to/import-a-repository/) and [Design with the assistant](/how-to/design-with-the-assistant/).

## Edit an element

1. Select the element. As an alternative, right-click it and select **Edit details**.
2. In the detail panel, on the tab **Details**, change **Name**, **Description**, **Technology**, **Status**, **Icon** or **Tags**.

The tab **Connections** lists the relationships of the element, outgoing and incoming. Select a row to go to the other element. Select **Edit** on a row to open the relationship.

**Status** is **Current**, **Planned** or **Deprecated**.

To copy an element, right-click it and select **Duplicate**, or press `Ctrl+D` or `Cmd+D`.

To delete an element, select the **⋯** button in the header of the detail panel, then select **Delete element**. As an alternative, right-click the element and select **Delete**. Vellum refuses to delete an element when an operation, a channel, a message or a scenario names it or an element inside it. The answer is `409` and the message lists the references. Remove or reassign them first.

To change more than one element, select two or more elements. The toolbar at the top shows the count as **selected**. Set the **Status**, enter a tag in **Add tag…** and press `Enter`, or select **Delete selected**.

`Ctrl+Z` or `Cmd+Z` reverses the last canvas action. Add `Shift` to do it again.

## Add a relationship

1. Drag from the handle of the source element to the target element.
2. Select the relationship.
3. In the **Relationship** panel, enter a **Label** and a **Technology**.
4. Set the **Line Style** to **Curved (Bezier)**, **Straight** or **Right-angle (Step)**.

To delete it, select the **⋯** button in the header of the panel, then select **Delete relationship**. Confirm **Delete this relationship?**.

A relationship has a kind: `structural` or `behavioural`. A relationship that you draw is `structural`. The app has no control for the kind. To set it, send `kind` in `POST /api/projects/{projectId}/relationships`.

The server refuses a relationship when an end does not exist. The messages are `Source element not found` and `Target element not found`.

## Find an element

- Press `Ctrl+F` or `Cmd+F`. Enter text in **Search name, kind, technology, truth label...**. Press `Enter` to go to the next match. Press `Escape` to close.
- Press `Ctrl+K` or `Cmd+K`, or select **Search** in the left toolbar. Enter text in **Search elements, actions...** and select an element.

The command palette also has these actions: **Add element**, **Toggle status lens**, **Toggle metadata lens**, **Toggle messages lens**, **Toggle truth labels**, **Tidy layout**, **Zoom to fit** and **Go to top level**.

## Arrange and save a diagram

A diagram is a saved view of the model. It has a name and a position for each element. Positions are layout. They are not part of the model and they carry no meaning.

1. Drag the elements. Vellum saves each position in the diagram.
2. To let Vellum arrange the diagram, press `T`, or right-click the canvas and select **Tidy layout**.
3. To see the full diagram, right-click the canvas and select **Zoom to fit**.
4. To add a diagram, select **Create new** in the left toolbar, select **New Diagram**, enter a **Diagram name** and select **Create**.
5. To open a diagram, select **Project files** in the left toolbar and select the diagram.

Each diagram shows the same model. An element that you delete is gone from each diagram.

The app does not rename or delete a diagram. Use `PATCH /api/projects/{projectId}/views/{viewId}` and `DELETE /api/projects/{projectId}/views/{viewId}`.

## Read the truth labels

Each element and relationship has a truth label.

| Label | Value in the API |
|---|---|
| **Declared** | `declared` |
| **Observed in source** | `observed-in-source` |
| **Inferred** | `inferred` |
| **Measured** | `measured` |
| **Unknown** | `unknown` |

1. Press `4`, or select **Toggle truth labels** in the command palette.
2. Read the **Truth labels** panel. It counts the labels and the **Structural** and **Behavioural** relationships that are on the screen.
3. Press `4` again to close the panel.

An element that you draw is **Declared**. An element from an import has the label of its evidence. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

The other lenses are `1` for status, `2` for metadata and `3` for messages.

## Add JSON schemas and messages

A message is data that one element produces and other elements consume. A message can name a JSON schema.

The app shows messages and schemas. It does not create them. Create them with the API.

1. Create the schema. The `content` is the JSON Schema as a string.

   ```sh
   curl -X POST "http://localhost:8080/api/projects/<project-id>/schemas" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"id":"<schema-uuid>","name":"OrderPlaced","description":null,"content":"{\"type\":\"object\",\"properties\":{\"orderId\":{\"type\":\"string\"}}}"}'
   ```

2. Create the message. Name the element that produces it and the elements that consume it.

   ```sh
   curl -X POST "http://localhost:8080/api/projects/<project-id>/messages" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"id":"<message-uuid>","name":"OrderPlaced","description":null,"producerId":"<element-id>","consumerIds":["<element-id>"],"schemaId":"<schema-uuid>","tags":[]}'
   ```

3. In the app, press `3` to show the messages on the canvas.
4. Select a message. The detail panel shows **Description**, **Producer**, **Consumers**, **Schema** and **Tags**.

The schema rules are these.

| Rule | Message in the `400` answer |
|---|---|
| The content is JSON. | `Content is not valid JSON` |
| The content is a JSON object. | `JSON Schema must be a JSON object` |
| The object has `type`, `properties` or `$schema`. | `JSON Schema must have a 'type', 'properties', or '$schema' property` |
| The content has at most 262144 characters. | `A schema holds at most 262144 characters; this one has <n>` |

A message with a producer that does not exist gets `Producer element not found`.

Change a schema with `PATCH /api/projects/{projectId}/schemas/{schemaId}` and a message with `PATCH /api/projects/{projectId}/messages/{messageId}`. Vellum refuses to delete a schema that the model still uses. The answer is `409`.

The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make the write calls on this page: those endpoints are not open to agent tokens and answer `403`.

## Next steps

- Operations, channels, subscriptions and scenarios: [Trace a scenario](/how-to/trace-a-scenario/).
- Text documents for the project: [Write project documents](/how-to/write-project-docs/).
- Copies of the model: [Save revisions and compare variants](/how-to/compare-variants/).
