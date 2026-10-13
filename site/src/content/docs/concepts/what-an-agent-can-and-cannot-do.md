---
title: What an agent can and cannot do
description: The line between a coding agent and a person, and why Vellum holds it on the server.
---

This page explains what a coding agent does in Vellum and what only a person does.

## The line

An agent works with an agent token. A person works in a signed-in session. Vellum decides which one a request is from the way it authenticated. A request cannot say that it is a person.

| An agent can | Only a person can |
|---|---|
| Read the model, the contracts, the scenarios and the promises. | Approve a promise. |
| Play a scenario on the declared design. | Accept an import proposal or an AI proposal. |
| Export the SDD, the bundle and a brief. | Change the model. |
| Propose a draft promise and its obligations. | Change settings, secrets and members. |
| Run a deterministic check. | Create or revoke an agent token. |
| Record the result of a test as evidence. | Set the requirement ID prefix. |

A token with every scope, made by an owner, still cannot do what is in the right column. No scope gives it.

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

Vellum keeps the two apart. The description of a tool and the notice of a result are text of the server. The names and the descriptions of a model are inside `data` and nowhere else.

An agent that follows an instruction it found in a model name is wrong. The skill tells agents this. The design does not depend on it: a token cannot approve, accept or change settings, whatever an agent was told.

## The scopes

| Scope | Lets a token |
|---|---|
| `read` | Read, and play a scenario. It writes nothing. |
| `propose` | Write drafts, obligations, runs and evidence. |
| `export` | Use the build endpoints. |

A token works in one workspace. An owner can restrict it to one project. A token has no more access than the user who created it. When that user leaves the workspace, the token stops.

## Where the rule is

The rule is on the server, in one place. Every endpoint is one of these: open with no credential, for a person only, open to a token with a named scope, or for a signed-in session. A token gets 403 on every endpoint that is not open to it.

An MCP tool calls the same endpoint as the HTTP API. It gets the same check, the same validation and the same limits. No tool exists for an act of a person.

## What the MCP tools leave out

The HTTP API lets a token with the `propose` scope start a new revision of a promise, change or remove an obligation of a draft, and delete a promise that was never approved. No MCP tool does these. An agent that needs one of them uses the HTTP endpoint, with the same token.
