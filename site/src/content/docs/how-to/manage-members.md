---
title: Manage workspaces, members and projects
description: Create workspaces and projects, add and remove members, and rename or delete through the API.
---

This guide shows you how to create a workspace and a project, how to give a person access and how to rename or delete. The app creates workspaces and projects. All other tasks on this page use the API. The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make these calls: these endpoints are not open to agent tokens and answer `403`.

## Know the roles

A workspace holds projects, settings, secrets and members. A member has one role in the workspace. The role applies to each project of the workspace.

| Role | Can do |
|---|---|
| `Viewer` | Read the projects, the models, the documents, the scans, the review and the estimates. Preview the merge of a draft. |
| `Editor` | All that a viewer can do. Create and rename projects. Change the model, the scenarios, the documents, the drafts, the variants and the cost sheet. Start scans and decide proposals. Write the draft of a promise. |
| `Owner` | All that an editor can do. Add and remove members. Rename the workspace. Delete a project. Change the settings and the secrets. Create agent tokens. Test the AI provider and the GitHub connection. Change the price book. Approve a promise. Accept or reject a decision. Authorise a load-test target. |

A person who is not a member of the workspace gets `403` for each request on it. A member with a role that is too low gets `403` with the text `Requires at least <role> role`.

The person who creates a workspace is its first owner.

## Create a workspace

1. Open `http://localhost:8080` and sign in.
2. Select **Create Workspace**.
3. Enter a **Workspace name** and confirm.

The request is `POST /api/workspaces` with `id` (a new UUID) and `name`.

## Create a project

1. Below **Workspaces**, select the workspace.
2. Select **+ New Project**.
3. In **Create Project**, enter a **Project name** and confirm.

The request is `POST /api/workspaces/{workspaceId}/projects` with `id` (a new UUID), `name` and `description`. You need the editor role.

## Add a member

You need the owner role. The person must already have an account on this instance.

Know these limits first.

- The request takes the account ID of the person. It does not take an email address.
- Vellum has no endpoint that finds an account by email. The person must give you the ID.
- Vellum sends no invitation. The person has access at once.

1. Ask the person to sign in and to open `http://localhost:8080/api/auth/me` in the same browser. The answer has their `id`.
2. Send the request. The `role` is `Owner`, `Editor` or `Viewer`.

   ```sh
   curl -X POST "http://localhost:8080/api/workspaces/<workspace-id>/members" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"userId":"<account-id>","role":"Editor"}'
   ```

3. Read the answer. The status is `201`.

| Status | Text | Cause |
|---|---|---|
| `400` | `Must be the ID of an existing user.` | No account has this ID. |
| `400` | `Must be Owner, Editor, or Viewer` | The role is not one of the three. |
| `409` | `User is already a member` | The person is a member. |

Vellum writes each added and removed member to the audit log of the workspace: `GET /api/workspaces/{workspaceId}/audit`.

## Know what is missing for members

- No endpoint lists the members of a workspace. The audit log shows who was added and removed.
- No endpoint changes a role. Remove the member, then add the member again with the new role.
- The app has no page for members.
- When registration is closed, nobody can create an account. Vellum has no endpoint that lets an administrator add one. The instance setting **Let people register themselves** has the hint **With this off, only an administrator adds accounts.** No such function exists. To add a person, the instance administrator switches on **Let people register themselves**, the person registers, and the administrator switches it off again. See [Configure settings](/how-to/configure-settings/).

## Remove a member

You need the owner role.

```sh
curl -X DELETE "http://localhost:8080/api/workspaces/<workspace-id>/members/<account-id>" \
  -b vellum.cookies
```

The status is `204`. The person loses access to each project of the workspace at once.

| Status | Type | Cause |
|---|---|---|
| `404` | `not_found` | The account is not a member. |
| `409` | `last_owner` | The member is the only owner. A workspace must have at least one owner. Add a second owner first. |

## Rename a workspace or a project

The app has no control for this.

- Workspace, owner role: `PATCH /api/workspaces/{id}` with `name`.
- Project, editor role: `PATCH /api/projects/{projectId}` with `name` and `description`.

```sh
curl -X PATCH "http://localhost:8080/api/projects/<project-id>" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"name":"Orders platform","description":null}'
```

## Delete a project

You need the owner role. The app has no control for this.

```sh
curl -X DELETE "http://localhost:8080/api/projects/<project-id>" \
  -b vellum.cookies
```

The delete cannot be reversed. Vellum also deletes the scans, the promises, the cost data, the review data, the stress profiles and runs, the load-test data and the agent tokens of the project. Make a backup first if you can need the project again: see [Back up and restore](/how-to/back-up-and-restore/).

Vellum has no endpoint that deletes a workspace.
