---
title: What an agent can and cannot do
description: The line between a coding agent and a person, and why Vellum holds it on the server.
---

This page explains what a coding agent does in Vellum and what only a person does.

## The line

An agent works with an agent token. A person works in a signed-in session. Vellum decides which one a request is from the way it authenticated. A request cannot say that it is a person.

| An agent can | Only a person can |
|---|---|
| Read the model, the contracts, the scenarios, the promises and their runs. | Approve a promise, or a revision that changes or retires one. |
| Play a scenario on the declared design. | Accept or reject a decision. Answer or drop a question. |
| Run the deterministic review, and read the decisions and the open questions. | Accept an import proposal, a proposal of the assistant or a proposal of an agent. |
| Export the SDD, the bundle and a brief. | Change the model. |
| Propose a draft promise and its obligations. | Change settings, secrets and members. |
| Change the draft of a promise, also one that a person wrote. | Create or revoke an agent token. |
| Start a new draft revision of an approved promise, also one that retires it. | Set the requirement ID prefix. |
| Propose a decision and open a question. | Answer a finding of the review. |
| Propose additions to the model and a flow, as a draft proposal. | Delete a promise that a person wrote or changed. |
| Run a deterministic check. | Change or remove an obligation that a person wrote or changed. |
| Record the result of a test as evidence. | |
| Delete a draft promise that agent tokens alone wrote. | |
| Change or remove an obligation that a token added and no person changed since. | |

A token with every scope, made by an owner, still cannot do what is in the right column. No scope gives it.

Everything in the left column that writes, writes a draft or a proposal. It changes no approved promise and no fact of the model. A person decides what becomes of it.

## What an agent writes names the agent

A person who decides must know who wrote what they read. So Vellum records the token on everything a token writes.

- `origin` says who wrote a promise, a decision, a question or a proposal first. For a token the value is `agent-suggested`, or `agent-token:<id>` on a proposal. A request cannot set it.
- `lastChangedBy` on a draft revision says who changed it last: `user:<id>` or `agent-token:<id>`. A token can change a draft that a person wrote. The origin stays `human`, and `lastChangedBy` names the token.
- `source` on an obligation is `generated`, `manual` for a person or `agent` for a token.

The app shows a notice on a draft that an agent token changed after the last change by a person. The owner reads that notice before the owner approves.

A token starts a new draft revision of an approved promise. That revision can retire the promise. The approved revision stays in force. The draft does nothing until an owner approves it in the app.

A token does not remove the work of a person. It deletes a draft promise only when agent tokens alone wrote it. It changes or removes an obligation only when a token added it and no person changed it since. For anything else the answer is 403 with the type `human_only`.

## Why

Vellum holds what a team agreed to build. An agent that builds the system must not also decide what the system has to do.

- If an agent could approve a promise, it could approve the promise that its own code passes.
- If an agent could change the model, it could change the design to match what it wrote.
- If an agent could accept a proposal, an inference could become a fact with no person in between.

So an agent proposes and a person decides. A draft that an agent writes has the origin `agent-suggested`. It is not a requirement until an owner approves it.

## Evidence, not verdicts

An agent records the result of a test. Vellum then reads the run from the system that holds it. If Vellum cannot read the run there, the result stays a claim and the obligation stays `not-run`.

No tool returns "verified" for a promise. A result is `passed`, `failed`, `not-run` or `unsupported`. It holds for the inputs that its run names. When an input moves, the result is stale.

## Text is data

A model holds text that people typed, text from repositories and text from an AI. An agent receives that text in fields of a tool result. It is never an instruction.

Vellum keeps the two apart. The description of a tool and the notice of a result are text of the server. In a result that worked, the names and the descriptions of a model are inside `data` and nowhere else.

A tool error is another document. Its `title`, its `detail` and its field messages are sentences of the server. A sentence can quote a name or a value from the model or from the call, for example the name of a thing that exists already. That text is data too.

An agent that follows an instruction it found in a model name is wrong. The skill tells agents this. The design does not depend on it: a token cannot approve, accept or change settings, whatever an agent was told.

## The scopes

| Scope | Lets a token |
|---|---|
| `read` | Read, play a scenario and run the review. It writes nothing. |
| `propose` | Write drafts, obligations, proposed decisions, questions, draft proposals, runs and evidence. |
| `export` | Use the build endpoints. |

A token works in one workspace. An owner can restrict it to one project. A token has no more access than the user who created it. When that user leaves the workspace, the token stops.

## Where the rule is

The rule is on the server, in one place. Every endpoint is one of these: open with no credential, for a person only, open to a token with a named scope, or for a signed-in session. A token gets 403 on every endpoint that is not open to it.

An MCP tool calls the same endpoint as the HTTP API. It gets the same check, the same validation and the same limits. No tool exists for an act of a person.

## What the MCP tools leave out

The HTTP API has four calls for a token with the `propose` scope that no MCP tool makes.

- Start a new draft revision of an approved promise: `POST /api/projects/{projectId}/promises/{promiseId}/revise`. With `{ "retire": true }` the draft retires the promise when an owner approves it.
- Change an obligation of a draft: `PUT …/promises/{promiseId}/obligations/{obligationKey}`.
- Remove an obligation of a draft: `DELETE …/promises/{promiseId}/obligations/{obligationKey}`.
- Delete a promise that was never approved: `DELETE …/promises/{promiseId}`.

The last three work only on what agent tokens alone wrote. For a promise or an obligation that a person wrote or changed, the answer is 403 with the type `human_only`.

An agent that needs one of these calls uses the HTTP endpoint, with the same token.

## Limits on what an agent writes

- One user makes at most 120 draft writes in a minute: create a promise, change a draft, add or change an obligation, generate obligations. A person and the tokens that act for that person share the limit.
- One user submits at most 30 proposals in a minute.
- A project holds at most 50 undecided proposals, of the assistant and of agents together. A person accepts, rejects or deletes one to make room.
