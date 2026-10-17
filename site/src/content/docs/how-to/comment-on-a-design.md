---
title: Comment on a design
description: Start a comment thread on an element or another target, reply, mention a member, resolve, raise a thread as a question or a decision, read the inbox and send comment events to a webhook.
---

This guide shows you how to discuss a design in comment threads. [Comments are not facts](/concepts/comments-are-not-facts/) explains what a thread attaches to and why no export holds it.

You need the Commenter role or a higher role to write. A Viewer reads the threads and cannot write. See [Manage members](/how-to/manage-members/#know-the-roles).

The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot use these endpoints: the answer is `403`.

## Start a thread on an element

1. Open the project.
2. On the canvas, select the element or the relationship.
3. In the detail panel, go to **Comments** and select **New comment**.
4. Write the comment and select **Comment**.

A marker with the number of open threads shows on the element on the canvas. Select the marker to open the threads of the element.

To start a thread on the project, select **Comments** in the toolbar on the left, then select **On the project**. The **Comments** panel lists the threads of the model that is shown, with the tabs **Open**, **Resolved**, **Detached** and **All**.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/threads" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"target":{"kind":"element","id":"<element-id>"},"body":"Why is this call synchronous?"}'
```

The answer is `201` with the thread. The thread has `id`, `target` (`kind`, `id`, `stepId`, `name`), `modelVersion` (the version of the model stream `modelStreamId` when the thread was made), `modelStreamId`, `status`, `createdBy`, `createdAt` and `comments`.

| Field | Meaning |
|---|---|
| `target.kind` | `project`, `element`, `relationship`, `message`, `operation`, `channel`, `step`, `promise`, `document` or `decision` |
| `target.id` | The stable ID of the target. For `project`, the project ID. For `step`, the scenario ID. |
| `target.stepId` | The step ID in the scenario, for `step` only. |
| `draftId` | Optional. The ID of an open draft. The target must exist on that draft. Leave it out for main. |
| `mentions` | Optional. The user IDs of the members that the text mentions. At most 20. |
| `id` | Optional. A UUID. A second request with the same ID gives the thread that exists. |

| Status | Cause |
|---|---|
| `400` | The body is empty or too long, the kind is unknown, or no target with this ID exists on this branch. |
| `403` | Your role is Viewer, or you are not a member of the workspace. |
| `409` | The draft is merged or abandoned, or the ID is in use. |

## Start a thread on a promise, a decision, a page or a flow step

Each of these has a **Comments** section with the same controls.

- **Promise**: select **Promises** in the toolbar, open the promise, and go to **Comments** at the end of the dialog.
- **Decision (ADR)**: select **Review**, then **Decisions**, open the decision, and go to **Comments**.
- **Document page**: open the page and select **Comments** in its header. The threads show beside the page.
- **Flow step**: select **Scenarios**, edit the scenario, and select the comment button beside the step ID. Only a saved step takes comments.

The lists of promises, decisions, documents and scenarios show the number of open threads beside each item. For a scenario, the number counts the threads on all its steps.

## Reply

1. Open the thread.
2. Write the reply and select **Reply**.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/replies" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"body":"Because the stock check must hold before the order is accepted."}'
```

A reply is on one level. You cannot reply to a reply.

## Mention a member

1. In the text, type `@`.
2. Select the member in the list.

The member gets an entry in the inbox. You can mention each member of the workspace, also a Viewer. In the API, send the user IDs in `mentions`. A user ID that is not a member of the workspace gives `400`.

## Edit or delete a comment

Only the author edits a comment. The comment then shows **edited** and the time. Vellum keeps no earlier text.

```sh
curl -X PATCH "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/comments/<comment-id>" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"body":"Why is this call synchronous and not a message?"}'
```

The author or an Owner deletes a comment. The comment stays in the thread with the text **Comment deleted**. When an Owner deletes the comment of another person, the audit log records `comment.deleted-by-owner` with the IDs of the comment, the thread, the project and the author.

```sh
curl -X DELETE "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/comments/<comment-id>" \
  -b vellum.cookies
```

## Resolve or reopen a thread

The author of the thread, an Editor or an Owner does this.

1. Open the thread.
2. Select **Resolve**. To open it again, select **Reopen**.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/resolve" -b vellum.cookies
curl -X POST "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/reopen" -b vellum.cookies
```

## List the threads

`GET /api/projects/{projectId}/threads` gives the threads of main. Add `draftId=<draft-id>` for the threads of a draft, `targetId=<id>` for the threads of one target, and `status=open`, `resolved` or `detached`.

A thread whose target was deleted has the status `detached` and the last name of the target. It has no marker on the canvas. Find it on the tab **Detached** of the **Comments** panel.

## Raise a thread as a question or a decision

A thread can become an item of the review. The item cites the thread by its ID.

- **Raise as open question** needs the Commenter role. Vellum makes an open question with the text of the first comment and the citation. The question is about the target of the thread. A second request gives the same question.
- **Raise as decision** needs the Editor role. You give a title and the decision text. Vellum makes a proposed decision. Its context is the text of the first comment and the citation. Only an Owner accepts it. See [Record a decision](/how-to/record-a-decision/).

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/raise-question" -b vellum.cookies

curl -X POST "http://localhost:8080/api/projects/<project-id>/threads/<thread-id>/raise-decision" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"title":"Check stock with a synchronous call","decision":"The order service calls the stock service before it accepts an order."}'
```

When the first comment is deleted, the thread has no text to raise, and the answer is `409`.

## Read the inbox

The inbox lists the comments that mention you, and the replies on threads that you started or replied to. You get no entry for your own comment. The inbox also lists the proposals that an agent submitted with an agent token that you created. See [Decide on a proposal from an agent](/how-to/design-with-the-assistant/#decide-on-a-proposal-from-an-agent).

1. In the top bar, select the bell. The number is the count of unread entries.
2. Select an entry to open the thread. The entry is then read.
3. To mark all entries read, select **Mark all read**.

```sh
curl "http://localhost:8080/api/inbox" -b vellum.cookies
curl -X POST "http://localhost:8080/api/inbox/<item-id>/read" -b vellum.cookies
curl -X POST "http://localhost:8080/api/inbox/read-all" -b vellum.cookies
```

The answer of `GET /api/inbox` has `unread` and `items`. An item of a proposal has the reason `proposal` and a `proposal` member with its `id`, `kind`, `name`, `items` and `pending`. Its `threadId`, `commentId` and `target` are null. Add `unreadOnly=true` for the unread entries only. The inbox shows no entry of a workspace that you are no longer a member of.

## Send comment events to a webhook

An owner sets the webhook URL of the workspace in **Settings** → **Notifications** → **Webhook URL**. See [Settings](/reference/settings/#notifications). Vellum then sends one `POST` for each of these events:

| Event | When |
|---|---|
| `comment.created` | A person starts a thread or replies. |
| `comment.mentioned` | A comment mentions members, or an edit mentions a member for the first time. |
| `comment.resolved` | A person resolves a thread. |

The body is JSON:

```json
{
  "event": "comment.created",
  "deliveryId": "<uuid>",
  "occurredAt": "2026-10-14T20:44:01.547957+00:00",
  "workspaceId": "<uuid>",
  "projectId": "<uuid>",
  "draftId": null,
  "threadId": "<uuid>",
  "commentId": "<uuid>",
  "author": { "userId": "<id>", "name": "Dev User" },
  "target": { "kind": "element", "id": "<uuid>", "stepId": null, "name": "Orders API" },
  "link": "http://localhost:8080/projects/<project-id>?thread=<thread-id>"
}
```

`comment.mentioned` also has `mentioned`, a list of `userId` and `name`. `comment.resolved` has no `commentId`, and `author` is the person who resolved the thread.

The body holds no comment text and no source text. A receiver that needs the text follows the link with a signed-in account.

`link` starts with `Vellum__PublicUrl`. With no such value, the hosted profile uses the host name of the request, and the local profile sends `null`. The Compose file sets it to `http://localhost:8080`. See [Bootstrap environment](/reference/bootstrap-environment/).

The request has these headers:

- `X-Vellum-Event`: the event.
- `X-Vellum-Delivery`: the delivery ID. A retry has the same ID.
- `X-Vellum-Timestamp`: the time of the send, in seconds since 1970-01-01 UTC.
- `X-Vellum-Signature`: only when the workspace has the secret `notifications:webhookSecret`. It is `sha256=` and the hex HMAC-SHA-256 of `<timestamp>.<body>` with the secret as the key.

To check a request, compute the HMAC of the timestamp, a dot and the raw body. Compare it with the header. Refuse a timestamp that is older than five minutes.

Vellum sends the event after the change commits. A `2xx` answer delivers it. For any other answer or a network failure, Vellum tries again after 30 seconds, then after 1, 2, 4 and 8 minutes. After 6 tries, Vellum stops. Each try reads the URL again and applies the URL rules of the instance policy. An event of a workspace with no webhook URL is not kept.
