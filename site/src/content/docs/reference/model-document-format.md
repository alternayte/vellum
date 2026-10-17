---
title: Model document format
description: The JSON and YAML document that the model export returns and the model import accepts, with every field, rule, limit and error.
---

A model document holds the model of one project: the elements, the relationships, the messages, the schemas and the contracts. `GET /api/projects/{projectId}/export` writes it. `POST /api/projects/{projectId}/import` reads it.

## Export and import in the app

1. Open the project.
2. Select **Model** in the sidebar, then the **Files** tab.
3. Below **Model document**, select **Export JSON** or **Export YAML**. The browser saves the file.

To import, you need the editor role.

1. Below **Model document**, select **Import a document…**.
2. Select the file for **Model document (JSON or YAML)**.
3. Read the preview. The server reads each entry and writes nothing. The list shows each entry that the import adds or changes. Select **Show the unchanged entries** to see the rest.
4. Select **Import <n> changes into main**.

When the server refuses an entry, the list shows **Refused** and the reason for that entry, and the dialog offers no import. Correct the document and select it again.

When main changes after the preview, the import is refused and the dialog offers **Preview again**.

The export and the import use main. They do not use the draft, the variant or the revision that the workbench shows.

This document is not the bundle. [Bundle and SDD format](/reference/bundle-and-sdd-format/) describes the export for a coding agent.

[Diagram export formats](/reference/diagram-export-formats/) describes the SVG, PNG, Mermaid and Structurizr DSL exports.

## Endpoints

| Endpoint | Role | Behaviour |
|---|---|---|
| `GET /api/projects/{projectId}/export` | Viewer | Returns the main model of the project. The query parameter `format` is `json` (the default) or `yaml`. |
| `POST /api/projects/{projectId}/import` | Editor | Reads a document and writes its content into the main model of the project. Answers `200` with the [import result](#import-result). |
| `POST /api/projects/{projectId}/import?dryRun=true` | Editor | Reads a document and writes nothing. Answers `200` with the import result, also when an entry is refused. |

The query parameter `expectedVersion` is optional. Give it the `modelVersion` of a preview. The import then answers `409` when main has changed since that preview.

## Formats

| Format | Export | Import |
|---|---|---|
| JSON | The default. Indented. Field names are in camel case. | Any `Content-Type` that does not contain `yaml`. |
| YAML | `?format=yaml`. The content type of the answer is `application/x-yaml`. | A `Content-Type` that contains `yaml`. |

The YAML document has the same fields as the JSON document.

The export orders each list by ID. The same model gives the same document.

## What the document holds

| Held | Not held |
|---|---|
| Elements, relationships and messages of the main model | Drafts and variants |
| Schemas of the project | Scenarios and trace results |
| Operations, channels, subscriptions and bindings | Promises, obligations and evidence |
| The truth label, the provenance and the stale mark of each fact | Views and canvas positions |
| The project name | Revisions and the event history |
| | Cost sheets, prices and stress models |
| | Decisions, findings and project documents |
| | Settings, credentials and members |

A model document is not a backup. See [Back up and restore](/how-to/back-up-and-restore/).

## Top level

| Field | Type | Meaning |
|---|---|---|
| `version` | string | The format version. The export writes `1.0`. The import does not read it. |
| `project.name` | string | The name of the project. The import does not read it and does not rename the project. |
| `elements` | list | See [Element](#element). |
| `relationships` | list | See [Relationship](#relationship). |
| `messages` | list | See [Message](#message). |
| `schemas` | list | See [Schema](#schema). |
| `operations` | list | See [Operation](#operation). |
| `channels` | list | See [Channel](#channel). |
| `subscriptions` | list | See [Subscription](#subscription). |
| `bindings` | list | See [Binding](#binding). |

The import reads a list that is missing as an empty list. A list that holds `null` is refused.

## Fact fields

Each element, relationship, message, operation, channel, subscription and binding has these three fields. A schema has `factStatus` and `provenance`, and no `stale`. See [Schema](#schema).

| Field | Type | Meaning |
|---|---|---|
| `factStatus` | string | The truth label: `declared`, `observed-in-source`, `inferred`, `measured` or `unknown`. Missing or empty means `declared`. |
| `provenance` | object or `null` | Where the fact came from. |
| `provenance.source` | string or `null` | A pointer to the source, 2000 characters at most. Not blank when present. |
| `provenance.revision` | string or `null` | The revision at which the source was read. Not blank when present. |
| `provenance.confidence` | number or `null` | From 0 to 1. |
| `provenance.acceptedBy` | GUID or `null` | The account that accepted the fact. |
| `provenance.acceptedAt` | timestamp or `null` | When the account accepted it. |
| `stale` | object or `null` | The stale mark. `null` or missing means the fact has no mark. |
| `stale.sinceRevision` | string | The commit of the first complete scan that did not find the evidence. Required in a mark. |
| `stale.reason` | string | A code, 100 characters at most, for example `evidence-not-found`. Required in a mark. |

The label sets these rules for the provenance.

| `factStatus` | Rule |
|---|---|
| `declared`, `unknown` | No provenance is necessary. |
| `observed-in-source` | `provenance.source` is required. |
| `inferred` | `provenance.source` and `provenance.confidence` are required. |
| `measured` | `provenance.source` is required. |

[Evidence and truth labels](/concepts/evidence-and-truth-labels/) explains the labels.

## Element

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `kind` | string | `actor`, `system`, `app`, `store` or `component`. |
| `name` | string | Required. |
| `description` | string or `null` | |
| `technology` | string or `null` | |
| `status` | string | `current`, `planned`, `deprecated` or `removed`. Required. |
| `parentId` | GUID or `null` | The element that contains this one. |
| `tags` | list of strings | Missing means an empty list. |
| `icon` | string or `null` | The icon name. |

The kind sets the parent.

| Kind | Parent |
|---|---|
| `actor`, `system` | None. `parentId` must be `null`. |
| `app`, `store` | An element of the kind `system`. Required. |
| `component` | An element of the kind `app`. Required. |

## Relationship

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `fromId` | GUID | The source element. It must be in the model. |
| `toId` | GUID | The target element. It must be in the model. |
| `label` | string or `null` | |
| `technology` | string or `null` | |
| `kind` | string | `structural` or `behavioural`. Missing or empty means `structural`. |
| `lineShape` | string or `null` | How the canvas draws the line. 100 characters at most. It has no meaning in the model. |

## Message

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `name` | string | Required. |
| `description` | string or `null` | |
| `producerId` | GUID | The element that produces the message. It must be in the model. |
| `consumerIds` | list of GUIDs | The elements that consume it. Each must be in the model. Missing means an empty list. |
| `schemaId` | GUID or `null` | The schema of the payload. |
| `tags` | list of strings | Missing means an empty list. |

## Schema

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `name` | string | Required. |
| `description` | string or `null` | |
| `content` | string | The schema text. Required. |
| `version` | integer | The version of the schema in the exporting project. The import does not read it. |
| `factStatus` | string or `null` | The truth label, as in [Fact fields](#fact-fields). |
| `provenance` | object or `null` | Where the schema came from, as in [Fact fields](#fact-fields). |

When a schema has no `factStatus` and no `provenance`, the import keeps the label of the schema in the project. A new schema then gets the label `declared`. A document of version 0.1.0 has neither field.

## Operation

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `ownerId` | GUID | The element that owns the operation. It must be in the model. |
| `name` | string | Required. |
| `transport` | string | `http`, `grpc` or `command`. |
| `requestSchemaId` | GUID or `null` | |
| `responseSchemaId` | GUID or `null` | |
| `timeoutMs` | integer | From 0 to 3600000. |
| `retry` | object or `null` | `null` or missing means no retry. |
| `retry.maxAttempts` | integer | From 1 to 10. |
| `retry.backoffMs` | integer | From 0 to 3600000. |

## Channel

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `name` | string | Required. |
| `kind` | string | `topic`, `queue` or `exchange`. |

## Subscription

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `channelId` | GUID | The channel. It must be in the model. |
| `consumerId` | GUID | The element that consumes. It must be in the model. |
| `retry` | object or `null` | As for an operation. |
| `deadLetterChannelId` | GUID or `null` | A channel in the model that is not `channelId`. |

## Binding

| Field | Type | Meaning |
|---|---|---|
| `id` | GUID | The stable ID. |
| `sourceChannelId` | GUID | A channel of the kind `exchange` or `topic`. |
| `targetChannelId` | GUID | A channel of the kind `queue` or `exchange`. Not the source channel. |
| `routingKey` | string or `null` | Not blank when present. |

One source, one target and one routing key name one binding. A second binding with the same three values is refused.

A document that was written before bindings existed has no `bindings` field. The import reads it as an empty list.

## ID rules

- Each ID is a GUID. The document gives the ID. The import keeps each ID, with one exception for schemas.
- An ID identifies one object across export and import. The import uses it to decide between an add and an update.
- A schema ID that appears two times in `schemas` is refused.
- A schema ID belongs to one project of an instance. When a schema of another project uses the ID, the import gives the schema a new ID in this project. It writes each `schemaId`, `requestSchemaId` and `responseSchemaId` of the document that refers to the schema with the new ID. It does not read or change the schema of the other project.
- The new ID is a function of the project ID and of the schema ID in the document. The same document gives the same IDs each time, so a second import changes those schemas and adds none.
- An import into the project that owns the schema ID keeps the ID.
- A schema ID must not be the ID of the model stream of the project.

## Import behaviour

The import is an upsert by ID.

- The project does not have to be empty.
- An object whose ID is not in the model is added.
- An object whose ID is in the model is updated to the values of the document. A nullable field that is `null` or missing in the document is cleared.
- An object that is in the model and not in the document does not change. The import removes nothing.
- The kind of an element that exists cannot change. The import refuses the document.
- The `fromId` and the `toId` of a relationship that exists do not change.
- A fact that has a stale mark keeps it. A fact that has no mark takes the mark of the document.
- A `schemaId` must name a schema in the document or a schema that the project has.

The import reads the sections in this order: elements, relationships, messages, channels, operations, subscriptions, bindings, schemas. In each section it reads the entries in document order, with one exception: it reads an element after its parent. The export orders elements by ID, so a child can stand before its parent in a document.

The import is one transaction. If one entry is refused, nothing is written. The import reads the entries that follow a refused entry, so the answer names each refused entry.

## Import result

| Field | Type | Meaning |
|---|---|---|
| `applied` | boolean | `true` when the import wrote. `false` for a preview. |
| `modelVersion` | integer | The version of main that the entries were read against. |
| `added`, `changed`, `unchanged`, `refused` | integer | The number of entries of each kind. |
| `items` | list | One item for each entry of the document. |
| `items[].kind` | string | `element`, `relationship`, `message`, `channel`, `operation`, `subscription`, `binding` or `schema`. |
| `items[].id` | GUID | The ID in the document. |
| `items[].name` | string or `null` | The name of the entry, or the label of a relationship. |
| `items[].change` | string | `added`, `changed`, `unchanged` or `refused`. |
| `items[].error` | string or `null` | Why the entry is refused. |
| `items[].importedAs` | GUID or `null` | For a schema whose ID belongs to another project: the ID that the schema has in this project. |

The import writes to the main model directly. It does not make a proposal. The repository import is a different feature that makes proposals: see [Import a repository](/how-to/import-a-repository/).

## Errors

Each error has a `type` and a `title`. The `title` names the first refused entry, for example `Element <id>: parentId: Parent element not found`. The list `errors` has one item for each refused entry: `field` is the kind and the ID, for example `element:<id>`, and `message` is the reason.

| Status | `type` | Cause |
|---|---|---|
| `400` | `invalid_export` | The body is not valid JSON or YAML, or a required field is missing or has the wrong type. |
| `400` | `invalid_export` | A list holds `null`. |
| `400` | `invalid_export` | A label is not in its set: `kind`, `status`, `transport`, `factStatus`. |
| `400` | `invalid_export` | A reference names an object that is not in the model: a parent, a source, a target, a producer, a consumer, an owner, a channel or a schema. |
| `400` | `invalid_export` | A model rule refuses an entry: a parent of the wrong kind, a name that is empty, a provenance that the label requires. |
| `400` | `invalid_export` | A schema ID repeats or is in use. |
| `409` | `conflict` | The request has `expectedVersion` and main has another version. |
| `400` | `import_too_large` | A list has more entries than its limit. |
| `413` | `import_too_large` | The body has more than 16777216 bytes. |
| `401` | | The request has no valid sign-in. |
| `403` or `404` | | The account has no editor role in the workspace of the project, or the project does not exist. |

## Limits

| Item | Limit |
|---|---|
| Body | 16777216 bytes |
| `elements` | 5000 |
| `relationships` | 10000 |
| `messages` | 5000 |
| `schemas` | 200 |
| `operations` | 5000 |
| `channels` | 5000 |
| `subscriptions` | 5000 |
| `bindings` | 5000 |

## Example

```json
{
  "version": "1.0",
  "project": { "name": "Shop" },
  "elements": [
    {
      "id": "11111111-1111-4111-8111-111111111111",
      "kind": "system",
      "name": "Shop",
      "description": null,
      "technology": null,
      "status": "current",
      "parentId": null,
      "tags": [],
      "icon": null,
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    },
    {
      "id": "22222222-2222-4222-8222-222222222222",
      "kind": "app",
      "name": "Orders API",
      "description": "Takes orders.",
      "technology": "ASP.NET Core",
      "status": "current",
      "parentId": "11111111-1111-4111-8111-111111111111",
      "tags": ["backend"],
      "icon": null,
      "factStatus": "observed-in-source",
      "provenance": {
        "source": "github.com/acme/shop@abc123:src/Orders/Program.cs:1-20",
        "revision": "abc123",
        "confidence": null,
        "acceptedBy": null,
        "acceptedAt": null
      },
      "stale": null
    },
    {
      "id": "33333333-3333-4333-8333-333333333333",
      "kind": "store",
      "name": "Orders database",
      "description": null,
      "technology": "PostgreSQL",
      "status": "current",
      "parentId": "11111111-1111-4111-8111-111111111111",
      "tags": [],
      "icon": null,
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    }
  ],
  "relationships": [
    {
      "id": "44444444-4444-4444-8444-444444444444",
      "fromId": "22222222-2222-4222-8222-222222222222",
      "toId": "33333333-3333-4333-8333-333333333333",
      "label": "Reads and writes",
      "technology": "SQL",
      "kind": "structural",
      "lineShape": null,
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    }
  ],
  "messages": [
    {
      "id": "55555555-5555-4555-8555-555555555555",
      "name": "OrderPlaced",
      "description": null,
      "producerId": "22222222-2222-4222-8222-222222222222",
      "consumerIds": [],
      "schemaId": "66666666-6666-4666-8666-666666666666",
      "tags": [],
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    }
  ],
  "schemas": [
    {
      "id": "66666666-6666-4666-8666-666666666666",
      "name": "OrderPlaced",
      "description": null,
      "content": "{\"type\":\"object\",\"properties\":{\"orderId\":{\"type\":\"string\"}}}",
      "version": 1,
      "factStatus": "declared",
      "provenance": null
    }
  ],
  "operations": [
    {
      "id": "77777777-7777-4777-8777-777777777777",
      "ownerId": "22222222-2222-4222-8222-222222222222",
      "name": "POST /orders",
      "transport": "http",
      "requestSchemaId": null,
      "responseSchemaId": null,
      "timeoutMs": 2000,
      "retry": { "maxAttempts": 1, "backoffMs": 0 },
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    }
  ],
  "channels": [
    {
      "id": "88888888-8888-4888-8888-888888888888",
      "name": "orders",
      "kind": "topic",
      "factStatus": "declared",
      "provenance": null,
      "stale": null
    }
  ],
  "subscriptions": [],
  "bindings": []
}
```
