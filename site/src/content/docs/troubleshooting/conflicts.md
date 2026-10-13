---
title: Conflicts and refused requests
description: The 409, 428, 403, 413 and 429 answers of Vellum, where each one occurs and how to continue.
---

Vellum refuses a write that does not fit the state that is stored now. A refused write changes nothing. This page lists the refusals by status and type.

An error answer has these fields.

```json
{
  "type": "conflict",
  "title": "The settings changed after you read them",
  "detail": "The current version is 4. Read the settings again, apply your change and save.",
  "errors": null
}
```

## Summary

| Status | `type` | Where | Fix |
|---|---|---|---|
| `409` | `conflict` | A model write while another write ran | Load the data again, then send the change again. |
| `409` | `conflict` | A new item with an ID that is in use | Use a new UUID. |
| `409` | `conflict` | A revision, a variant or a member that exists | Use another name, or do nothing. |
| `409` | `conflict` | A delete of an element or a schema that is in use | Remove the references first. |
| `409` | `conflict` | Settings with an old `If-Match` | Read the settings again, apply the change, save. |
| `428` | `precondition_required` | Settings with no `If-Match` | Send the version that you read. |
| `409` | `revision_changed` | The approval of a promise | Read the promise again. Approve what you read. |
| `409` | `decision_changed` | The accept or reject of a decision | Read the decision again. |
| `200` with `failed` | none | A decision on import proposals | Read `failed`. Nothing was applied. |
| `409` | `comparison_scan` | A decision on the proposals of a comparison scan | Follow the ref, or scan the tracked ref. |
| `409` | `conflict`, `cost_sheet_conflict` | The merge of a draft | Preview again, or make the cost sheets agree. |
| `409` | `critique_stale` | A proposed decision of an old assistant answer | Ask the assistant again. |
| `409` | `last_owner` | The removal of a member | Add a second owner first. |
| `403` | `human_only` | An agent token on an endpoint for people | A person does it in the app. |
| `403` | `agent_not_allowed` | An agent token with no access to the endpoint | Use an endpoint that is open to agents, or another scope. |
| `403` | none | A member with a role that is too low | Ask an owner. |
| `413` | `payload_too_large` and others | A request body that is too large | Send less. |
| `429` | `rate_limited`, `scan_limit_reached` | Too many calls | Wait for `Retry-After`. |
| `503` | `busy` | Two writes blocked each other | Send the request again after one second. |

## The model changed while the request ran

**Symptom:** `409`, type `conflict`, title `The model changed while the request ran. Reload and try again.`

**Cause:** Each model is a stream of events with a version. A write reads the version, decides and appends. Another write appended between the read and the append. Vellum does not overwrite the other change.

**Fix:**

1. Load the model again.
2. Check that your change is still correct.
3. Send the change again.

A related answer is `503` with the type `busy` and the title `Another write held the model. Nothing was saved; try again.` The answer has `Retry-After: 1`. Send the same request again.

## An ID or a name is in use

| Title | Where | Fix |
|---|---|---|
| `This ID is already in use. Choose another ID.` | A new item with the ID of an item of another kind | Make a new UUID. |
| `Schema already exists in this stream` | `POST /api/projects/{projectId}/schemas` | Make a new UUID. |
| `A revision with this name already exists` | `POST /api/projects/{projectId}/revisions` | Use another name. |
| `A variant with this name already exists` | `POST /api/projects/{projectId}/variants` | Use another name. |
| `Revision id is already in use`, `Variant id is already in use` | The ID belongs to another project | Make a new UUID. |
| `A draft with this ID already exists.`, `A comment with this ID already exists.` | The ID exists with other content | Make a new UUID. |
| `User is already a member` | `POST /api/workspaces/{workspaceId}/members` | No fix is necessary. To change the role, remove the member and add the member again. |
| `The prefix of this project is already set and cannot be changed` | `PUT /api/projects/{projectId}/promises/prefix` | No fix. Issued requirement IDs contain the prefix. |

A create request with an ID and content that the project already has is not an error. Vellum answers with the stored item. You can send a create request again after a lost connection.

## An item is in use and cannot be deleted

**Symptom:** `409`, type `conflict`. The title is one of these.

- `Element '<name>' cannot be removed. It, or an element inside it, is referenced by <list>. Remove or reassign them first.`
- A sentence that names the items that use a schema.
- `The schema list is being rebuilt. Nothing was deleted; try again.`

**Fix:**

1. Read the list in the title.
2. Remove each reference, or point it at another item.
3. Send the delete again.

## The settings changed after you read them

**Symptom:** `PUT /api/workspaces/{workspaceId}/settings` or `PUT /api/instance/settings` answers `409`, type `conflict`, title `The settings changed after you read them`. The `detail` gives the current version.

**Cause:** Settings have a version that goes up by one on each save. The `GET` answer gives it in the body and in the `ETag` header. A save sends it back in `If-Match`. Your version is old, so another person saved after you read.

**Fix:**

1. Read the settings again.
2. Apply your change to the new copy.
3. Save with the new version.

```sh
curl -X PUT "http://localhost:8080/api/workspaces/<workspace-id>/settings" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -H 'If-Match: "4"' \
  -d @settings.json
```

## The If-Match header is missing

**Symptom:** `428`, type `precondition_required`, title `The If-Match header is missing`.

**Fix:**

1. Read the settings. Take the version from the `ETag` header.
2. Send the save with `If-Match: "<version>"`. The quotation marks are part of the value.

A value that is not a version in quotation marks gets `400` with `Must be the version in quotes, for example "3".`

## The promise changed before the approval

**Symptom:** `POST /api/projects/{projectId}/promises/{promiseId}/approve` answers `409`, type `revision_changed`, title `The draft is not the one that was read`. The answer gives the revision number and the content hash of the draft as it is now. The app shows the draft as it is now.

**Cause:** An approval names the revision and the content hash that the owner read. Another person, or an agent, changed the draft after that. Vellum does not let an approval bind text that the owner did not read.

**Fix:**

1. Read the promise again.
2. Approve what you read.

The same rule applies to a decision. `decision_changed` with the title `The decision is not the one that was read` means that the decision changed after you opened it. Read it again, then accept or reject.

## A proposal is already decided

**Symptom:** `POST /api/projects/{projectId}/imports/{importId}/proposals/decide` answers `200`. The list `failed` is not empty. The lists `accepted`, `rejected` and `relinked` are empty.

**Cause:** When `failed` holds an entry, Vellum applied nothing. Each entry has the `id` of a proposal and a `reason`.

| Reason | Fix |
|---|---|
| `The proposal is already accepted.` or `The proposal is already rejected.` | Another person decided it. Load the proposals again and take it out of the request. |
| `This is not a proposal of this import.` | Use the proposals of this scan. |
| `It depends on proposal <id>, which no longer exists. Scan the repository again.` | Start a new scan. |
| `Accepting it affects <list>. Send acknowledgeImpact: true to accept it anyway.` | Read the list. In the app, select **I accept that.** In the API, send `acknowledgeImpact: true`. |
| `A relink proposal is decided under 'relink', …` | Send the proposal in `relink`, not in `accept`. |

Check `failed` after each decision. The status `200` alone does not mean that the decision was applied.

## A comparison scan cannot be decided

**Symptom:** `409`, type `comparison_scan`. The title starts with `This scan read another ref than the one the accepted facts follow` or with `This scan read a commit and no ref.` It ends with `Its proposals are a preview and cannot be decided.`

**Fix:**

1. To accept from this ref, select **Follow this ref** on the scan. Then decide.
2. To keep the tracked ref, scan the tracked ref and decide on that scan.
3. A scan of a commit SHA can never be decided. Scan a branch or a tag.

See [Check the impact of a change](/how-to/check-change-impact/). A decision on a scan that did not complete gets `409` with the type `import_not_completed`.

## A draft does not merge

| Status | Type and title | Fix |
|---|---|---|
| `409` | `conflict`, `Main has changed since preview. Please re-preview.` | Preview again. Send the new `mainVersion` as `expectedMainVersion`. |
| `400` | `validation_error`, `<n> conflict(s) without resolution` | Send `keep_ours` or `take_theirs` for each conflict of the preview. |
| `409` | `cost_sheet_conflict`, `The cost sheet of the draft does not merge into the cost sheet of main` | Change the cost sheet of the draft or of main until the two agree. A cost sheet conflict has no resolution to choose. |
| `409` | `conflict`, `Draft is merged` or `Draft is abandoned` | Create a new draft. |
| `400` | `invalid_branch` | The `branchId` is not an open draft or variant of the project. |

See [Work in a draft](/how-to/work-in-a-draft/).

## The last owner cannot be removed

**Symptom:** `DELETE /api/workspaces/{workspaceId}/members/{memberUserId}` answers `409`, type `last_owner`, title `Cannot remove the last owner`.

**Fix:**

1. Add a second owner.
2. Remove the first owner.

See [Manage workspaces, members and projects](/how-to/manage-members/).

## An agent token is refused

| Status | Type | Cause | Fix |
|---|---|---|---|
| `403` | `human_only` | The endpoint is for a person signed in to Vellum. Examples are the approval of a promise and the accept of a decision. The title is `Only a person signed in to Vellum does this`. | A person signs in to the app and does it there. No scope and no role changes this. |
| `403` | `agent_not_allowed` | The endpoint is not open to agent tokens, the token lacks the scope, the token is restricted to another project, or its user left the workspace. | Call `GET /api/agent/capabilities` to see what the token can call. Create a token with the scope that the call needs. |
| `401` | none | No credential, a token that is expired or revoked, or a session cookie on an endpoint that takes agent tokens only. | Send a valid token. |

An agent token also deletes only a draft promise that agent tokens alone wrote. It changes or removes only an obligation that an agent token added and no person changed after that. For other promises and obligations the answer is `403` with `human_only`.

See [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

## The role is too low

**Symptom:** `403`. The text is `Requires at least Editor role` or `Requires at least Owner role`.

**Fix:**

1. Ask a workspace owner to do the task, or to give you the role. See [Manage workspaces, members and projects](/how-to/manage-members/#know-the-roles).

A person who is not a member of the workspace gets the same answer.

## The request body is too large

**Symptom:** `413`.

| Type | Title | Limit |
|---|---|---|
| `payload_too_large` | `The request body holds at most <n> bytes.` or `The request body is too large` | 65,536 bytes for an endpoint with no limit of its own. Sign-in and setup take 4 KB. A credential takes 32 KB. |
| `promise_too_large` | The content and the obligations of the revision are larger than the limit. | See [Promises and evidence](/reference/promises-and-evidence/#limits). |
| `import_too_large` | `An import document holds at most <n> bytes.` | The number in the title. |

**Fix:**

1. Read the number in the title or in `detail`.
2. Send a smaller body. Split a large schema or a large document.

Vellum refuses the body before it reads it.

## Too many requests

**Symptom:** `429` with a `Retry-After` header.

| Type | Where | Limit |
|---|---|---|
| `rate_limited`, title `Too many attempts` | `POST /api/auth/login` and `POST /api/setup` | 10 calls in one minute from one client address. |
| `rate_limited` | Promise runs and evidence | A number of records in one minute for one caller. |
| `scan_limit_reached` | `POST /api/projects/{projectId}/imports` | **Repository scans per day** for one workspace. The day is a UTC day. |

**Fix:**

1. Wait for the number of seconds in `Retry-After`.
2. Send the request again.
3. For `scan_limit_reached`, a workspace owner can increase the limit under **Settings → Usage limits**.

## The text cannot be stored

**Symptom:** `400`, type `invalid_text`, title `The request holds text that cannot be stored, such as the character U+0000. Nothing was saved.`

**Fix:**

1. Remove the control characters from the text.
2. Send the request again.

## See also

- [Use the HTTP API](/how-to/use-the-http-api/)
- [HTTP API reference](/reference/http-api/)
- [Stale results and Build Ready blockers](/troubleshooting/stale-results-and-build-ready/)
