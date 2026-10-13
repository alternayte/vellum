---
title: Manage workspaces, members and projects
description: Create workspaces and projects, add members, change roles and remove members in the app, and rename or delete through the API.
---

This guide shows you how to create a workspace and a project, how to give a person access and how to rename or delete. The app creates workspaces and projects and manages members. Renaming and deleting use the API. The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make these calls: these endpoints are not open to agent tokens and answer `403`.

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
2. Select **+ New Workspace**. When the account has no workspace, the button reads **Create Workspace**.
3. Enter a **Workspace name** and select **Create**.

The request is `POST /api/workspaces` with `id` (a new UUID) and `name`.

## Create a project

1. Below **Workspaces**, select the workspace.
2. Select **+ New Project**.
3. In **Create Project**, enter a **Project name** and select **Create**.

The request is `POST /api/workspaces/{workspaceId}/projects` with `id` (a new UUID), `name` and `description`. You need the editor role.

## Open the member list

1. Open `http://localhost:8080` and sign in.
2. Below **Workspaces**, select the workspace.
3. Select **Settings**.
4. Select **Members**.

The list shows each member with the name, the email address, the role and the date since which the person is a member. **Not recorded** stands for a membership that is older than this date field.

Each member of the workspace can read the list. Only an owner sees the controls that change it.

## Add a member

You need the owner role. The person must already have an account on this instance.

Know these limits first.

- Vellum sends no invitation. The person has access as soon as you add them.
- When registration is closed, a new person cannot create an account. See [Let a new person register](#let-a-new-person-register).

1. Open the member list.
2. Below **Add a member**, enter the **Email address of the account**.
3. Select a **Role**: **Viewer**, **Editor** or **Owner**.
4. Select **Add member**.

If the request fails, the form shows **No member was added.** and the reason.

| Reason | Cause |
|---|---|
| `No account on this instance has this email address. The person must create an account first.` | No account has this address. |
| `User is already a member` | The person is a member. Change the role in the list. |

## Change a role

You need the owner role.

1. Open the member list.
2. In the row of the member, select the new role in the **Role** list.

Vellum saves the role at once. The new role applies to the next request of the person.

A workspace must have at least one owner. When you change the role of the only owner, Vellum refuses, and the row shows `Cannot change the role of the last owner`. Make another member an owner first.

## Remove a member

You need the owner role.

1. Open the member list.
2. In the row of the member, select **Remove**.
3. Select **Remove member**.

The person loses access to each project of the workspace at once. The account stays.

When you remove the only owner, Vellum refuses, and the dialog shows `Cannot remove the last owner`. Add a second owner first.

When you remove yourself, you lose access to the workspace. An owner must add you again.

## Let a new person register

Only the instance administrator can do this.

1. Open the workspace settings.
2. Select **Instance**.
3. Switch on **Let people register themselves**.
4. Ask the person to open the sign-in screen, to select **Register** and to create the account.
5. Switch off **Let people register themselves**.
6. Add the person as a member.

The sign-in screen shows **Register** only while registration is open. It shows a button for GitHub or Google only when the instance has that provider. See [Configure settings](/how-to/configure-settings/).

Vellum has no function that lets an administrator create an account for another person.

## Use the API for members

`GET /api/workspaces/{workspaceId}/members` gives the list. Each member can call it. The answer has `yourRole` and `members`. Each member has `userId`, `email`, `displayName`, `role` and `createdAt`.

To add a member, give the `email` of the account or its `userId`, not both. The `role` is `Owner`, `Editor` or `Viewer`.

```sh
curl -X POST "http://localhost:8080/api/workspaces/<workspace-id>/members"   -b vellum.cookies   -H "Content-Type: application/json"   -d '{"email":"person@example.com","role":"Editor"}'
```

| Status | Text | Cause |
|---|---|---|
| `201` | | The person is a member. |
| `400` | `No account on this instance has this email address. The person must create an account first.` | No account has this address. |
| `400` | `Must be the ID of an existing user.` | No account has this ID. |
| `400` | `Must be Owner, Editor, or Viewer` | The role is not one of the three. |
| `409` | `User is already a member` | The person is a member. |

To change a role, send the new role.

```sh
curl -X PATCH "http://localhost:8080/api/workspaces/<workspace-id>/members/<account-id>"   -b vellum.cookies   -H "Content-Type: application/json"   -d '{"role":"Viewer"}'
```

To remove a member, send a delete.

```sh
curl -X DELETE "http://localhost:8080/api/workspaces/<workspace-id>/members/<account-id>"   -b vellum.cookies
```

The status of a removal is `204`.

| Status | Type | Cause |
|---|---|---|
| `404` | `not_found` | The account is not a member. |
| `409` | `last_owner` | The member is the only owner. A workspace must have at least one owner. Add a second owner first. |

Vellum writes each added member, each removed member and each changed role to the audit log of the workspace: `GET /api/workspaces/{workspaceId}/audit`. The app shows the log in **Settings**, then **Audit log**.

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
