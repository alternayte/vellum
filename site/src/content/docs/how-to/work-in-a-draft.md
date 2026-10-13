---
title: Work in a draft
description: Create a draft of the model through the API, change it, preview the merge, resolve conflicts and merge it into main.
---

This guide shows you how to change the model in a draft and how to merge the draft into main. You need the editor role.

The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make these calls: these endpoints are not open to agent tokens and answer `403`.

## Know the limit

The app has no control that creates or opens a draft. Drafts work through the API only. The **Branch** list in the **Costs** flyout shows the open drafts, so you can edit the cost sheet of a draft in the app.

## Create the draft

A draft is a copy of main at one version. Main can change while the draft is open.

1. Send the request. The `id` is a new UUID that you make.

   ```sh
   curl -X POST "http://localhost:8080/api/projects/<project-id>/drafts" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"id":"<new-uuid>","name":"add-outbox"}'
   ```

2. Read the answer. Keep `id` (the draft ID) and `streamId` (the branch ID).

```json
{
  "id": "7f0c1a52-3f0e-4d53-9a55-0f6f4a2f6c11",
  "projectId": "b1b0e6b0-1a0c-4c62-8a34-2a6a2a0a7d10",
  "name": "add-outbox",
  "streamId": "2b9d2c0e-9a0b-4a47-8a53-5f1a0c7a3e90",
  "baseStreamId": "5a3d8f6e-0b7a-4e0e-9d0b-0c9d1a1b2c3d",
  "forkVersion": 42,
  "status": "open",
  "createdBy": "0d7c5b1e-6a2f-4b3c-8d9e-1f2a3b4c5d6e",
  "createdAt": "2026-01-12T09:30:00+00:00",
  "mergedAt": null,
  "abandonedAt": null
}
```

`GET /api/projects/{projectId}/drafts` lists the drafts. `GET /api/projects/{projectId}/drafts/{draftId}` gives one.

## Change the draft

Add `?branchId=<streamId>` to a model request. The request then reads or writes the draft and not main.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/elements?branchId=<stream-id>" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"id":"<new-uuid>","kind":"store","name":"Outbox","description":null,"technology":"PostgreSQL","ownerId":null,"status":"planned","parentId":"<system-id>","tags":[]}'
```

The endpoints for elements, relationships, messages, operations, channels, subscriptions and bindings accept `branchId`. So do the cost endpoints, the scenario run, the stress run, the review findings and the build manifest.

A write to a draft that is merged or abandoned gets `400` with the error type `invalid_branch`.

To give the draft the cost sheet of main, send `POST /api/projects/{projectId}/costs/copy` with `?branchId=<streamId>` and the body `{"from":"main"}`.

## Add a comment

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/drafts/<draft-id>/comments" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"id":"<new-uuid>","body":"Why a store and not a table in the orders database?","entityId":"<element-id>","entityType":"element"}'
```

`entityId` and `entityType` are optional. They attach the comment to one part of the model.

- `GET /api/projects/{projectId}/drafts/{draftId}/comments` lists the comments.
- `PATCH /api/projects/{projectId}/drafts/{draftId}/comments/{commentId}` changes the `body`.
- `DELETE /api/projects/{projectId}/drafts/{draftId}/comments/{commentId}` deletes the comment.

You change and delete only your own comments. For a comment of another person the answer is `403` with `You can only edit your own comments` or `You can only delete your own comments`.

## Preview the merge

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/drafts/<draft-id>/merge/preview" \
  -b vellum.cookies
```

The preview writes nothing. A viewer can call it.

```json
{
  "autoResolved": [
    {
      "entityType": "element",
      "entityId": "c4a8f6de-58a3-4b55-93a3-1b8c0b6c5f01",
      "changeKind": "added",
      "resolvedValue": { "name": "Outbox", "kind": "store" }
    }
  ],
  "conflicts": [
    {
      "entityType": "element",
      "entityId": "e2c1d7aa-0f43-4a8e-b8f4-6a2f0d9e7b22",
      "conflictKind": "both_modified",
      "baseValue": { "name": "Orders API" },
      "oursValue": { "name": "Order API" },
      "theirsValue": { "name": "Orders Service" }
    }
  ],
  "mainVersion": 45,
  "costSheet": { "changes": 1, "conflicts": [] }
}
```

| Field | Meaning |
|---|---|
| `autoResolved` | The changes that merge with no decision. `changeKind` is `added`, `modified` or `removed`. |
| `conflicts` | The changes that need a decision. |
| `baseValue` | The value when you created the draft. |
| `oursValue` | The value in main now. |
| `theirsValue` | The value in the draft. |
| `mainVersion` | The version of main that the preview read. Send it with the merge. |
| `costSheet.changes` | The number of resources and assumptions that the merge changes in the cost sheet of main. |
| `costSheet.conflicts` | The reasons why the two cost sheets do not merge. |

The values in the example are short. A real value is the full element, relationship, message, operation, channel or subscription.

## Read the conflicts

| `conflictKind` | Meaning |
|---|---|
| `both_modified` | Main and the draft changed the same item in different ways. |
| `delete_modify` | One side deleted the item and the other side changed it. |
| `orphaned_child` | One side deleted an element and the other side put a child in it. |
| `referenced_by_contract` | One side deleted an element or a channel that an operation, a subscription or a binding of the other side names. |
| `dangling_operation` | The owner of an operation does not exist after the merge. |
| `dangling_subscription` | A subscription names an item that does not exist after the merge. |
| `dangling_binding` | A binding names an item that does not exist after the merge. |

For each conflict, choose one resolution.

| Resolution | Result |
|---|---|
| `keep_ours` | Main keeps its value. |
| `take_theirs` | Main gets the value of the draft. If the draft deleted the item, main deletes it. |

## Merge

An error answer has the fields `type`, `title`, `detail` and `errors`. The tables on this page give the `type` and the `title`.

1. Send the merge. Give one resolution for each conflict. Give the `mainVersion` of the preview as `expectedMainVersion`.

   ```sh
   curl -X POST "http://localhost:8080/api/projects/<project-id>/drafts/<draft-id>/merge/execute" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"resolutions":[{"entityId":"<entity-id>","resolution":"take_theirs"}],"expectedMainVersion":45}'
   ```

2. Read the answer. The `status` of the draft is `merged`.

The merge writes the model and the cost sheet together. If Vellum refuses the merge, it writes nothing.

| Status | Message | Fix |
|---|---|---|
| `409` | `Main has changed since preview. Please re-preview.` | Preview again. Send the new `mainVersion`. |
| `400` | `<n> conflict(s) without resolution` | Add a resolution for each conflict. |
| `400` | `Duplicate resolution for entity <id>` | Send one resolution for each `entityId`. |
| `409`, type `cost_sheet_conflict` | `The cost sheet of the draft does not merge into the cost sheet of main` | Read `detail`. Change the cost sheet of the draft or of main until the two agree. Merge again. |
| `409` | `Draft is merged` or `Draft is abandoned` | The draft is closed. Create a new draft. |
| `409` | A sentence that names an item that points at nothing | The resolutions give a model that is not valid. Choose other resolutions, or change the draft. |

A cost sheet conflict has no resolution to choose. The two sheets must agree before the merge.

## Abandon a draft

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/drafts/<draft-id>/abandon" \
  -b vellum.cookies
```

The `status` becomes `abandoned`. Main does not change. An abandoned draft cannot be opened again.
