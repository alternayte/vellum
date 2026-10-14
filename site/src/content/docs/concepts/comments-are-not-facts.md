---
title: Comments are not facts
description: Where comment threads live, what they attach to, what happens when a target goes away, and why no export holds them.
---

This page explains what a comment thread is in Vellum and what it is not.

## A comment is an opinion

The model holds the facts of the architecture. Each fact has a truth label and an origin. See [The model is the source of truth](/concepts/model-is-the-source-of-truth/).

A comment holds the opinion of one member. It has an author and a time. It has no truth label, because it states no fact.

So Vellum keeps comments out of the model:

- A comment is not an event of the model stream. A comment does not make a revision and does not change the version of the model.
- The model export, the SDD, the bundle and the agent brief contain no comment.
- An agent token cannot read or write comments.

A comment cannot change the model. To make a comment act on the design, raise it as an open question or as a decision. That item then goes through the normal review path, and a person decides it. See [Raise a thread](/how-to/comment-on-a-design/#raise-a-thread-as-a-question-or-a-decision).

## A thread attaches to a stable ID

A thread is about one target. The target is one of these, named by its stable ID:

| Kind | Target |
|---|---|
| `project` | The project itself |
| `element` | An element: an actor, a system, an app, a store, a component |
| `relationship` | A relationship between two elements |
| `message` | A message |
| `operation` | An operation, which is a contract that an element serves |
| `channel` | A channel: a topic, a queue or an exchange |
| `step` | One step of a scenario, named by the scenario ID and the step ID |
| `promise` | A promise |
| `document` | A page of the project documents |
| `decision` | A decision (ADR) |

A thread never attaches to a place on the canvas. The canvas shows a marker on the element or the relationship of a thread. When you move the element, the marker moves with it. When you show the element in a second view, the marker shows there too.

Each thread records the version of the model when it was made. So a later reader knows which state of the model the first comment was about.

## A thread is on main or on one draft

A thread on a draft is about the model of that draft. The target must exist on that draft.

When the draft merges, each open thread of the draft moves to main and keeps its target. A resolved thread stays with the draft as its history. A merged or abandoned draft takes no new reply.

## The status of a thread

| Status | Meaning |
|---|---|
| `open` | The discussion goes on. |
| `resolved` | A person closed the discussion. A thread can open again. |
| `detached` | The target does not exist any more. |

When a person deletes the target, the thread stays. Its status becomes `detached`, and it shows the last name of the target. For an element, a relationship, a message, an operation or a channel, Vellum reads that name from the history of the model, so a rename just before the delete shows. For other targets, the name is the one that Vellum last read.

When the target comes back with the same ID, for example after an import, the thread is attached again.

## One level of replies

A thread has a first comment and replies. A reply cannot have replies of its own. This keeps a discussion in one order of time.

## Who does what

The roles are in order: Viewer, Commenter, Editor, Owner. Each role can do all that the role before it can do.

| Action | Who |
|---|---|
| Read threads | Viewer and above |
| Start a thread, reply, mention a member | Commenter and above |
| Edit a comment | The author only |
| Delete a comment | The author, or an Owner |
| Resolve or reopen a thread | The author of the thread, an Editor, an Owner |
| Raise a thread as an open question | Commenter and above |
| Raise a thread as a decision | Editor and above |

A Commenter reads all that a Viewer reads. A Commenter cannot change the model, the scenarios, the documents or the drafts.

A deleted comment stays in the thread as a tombstone with no text. So the replies around it keep their place. When an Owner deletes the comment of another person, the audit log of the workspace records it with the IDs. The audit log never holds the text.

An edit replaces the text. The comment then shows **edited** and the time. Vellum keeps no earlier text.

## Where comments go outside the workspace

A comment goes to two places only:

- The inbox of a member that it mentions, and of the members who are in the thread.
- The webhook of the workspace, when an owner set one. The webhook event holds IDs, names, the author and a link. It holds no comment text and no source text. See [Comment on a design](/how-to/comment-on-a-design/#send-comment-events-to-a-webhook).
