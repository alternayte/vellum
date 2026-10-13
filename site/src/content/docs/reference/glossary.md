---
title: Glossary
description: The terms of Vellum in alphabetical order, each with a short definition and a link to the page that defines it.
---

Each entry gives the meaning of a term in Vellum and the page that defines it.

## A

**Actor**: An element kind: a person or an external system that uses the system. An actor has no parent. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

**Agent token**: A bearer token, with the prefix `vlm_agt_`, that lets a coding agent use the MCP endpoint and the HTTP API of one workspace. It has scopes and cannot do what only a person does. See [Connect a coding agent](/how-to/connect-a-coding-agent/) and [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

**App**: An element kind: a deployable part of a system, such as a service or a web client. Its parent is a system. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

**Assumption**: A stated input of an estimate that Vellum did not measure, such as a number of requests in a month. A cost sheet and a stress result list their assumptions. See [Cost model](/reference/cost-model/#assumption) and [Estimates are not measurements](/concepts/estimates-are-not-measurements/).

## B

**Binding**: A contract that routes messages from a source channel (an exchange or a topic) to a target channel (a queue or an exchange), with an optional routing key. See [Model document format](/reference/model-document-format/#binding).

**Brief**: One Markdown document for a selection of promises, scenarios and elements, for an agent that makes one change. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#the-brief).

**Build Ready**: The state of a pinned model that has no blocker. A blocker prevents Build Ready and does not prevent an export. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#build-ready-blockers).

**Bundle**: The zip that Vellum exports for a coding agent: `SDD.md`, the contract files, the scenarios, the promises, the accepted decisions and `manifest.json`. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#files-of-the-bundle).

## C

**Channel**: A contract for messaging: a topic, a queue or an exchange. See [Model document format](/reference/model-document-format/#channel).

**Comparison scan**: A repository scan of a ref that the model does not follow, or of a bare commit. You can read its proposals and you cannot accept them. It writes nothing to the model and marks no fact stale. See [Check change impact](/how-to/check-change-impact/).

**Component**: An element kind: a part inside an app. Its parent is an app. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

**Content hash**: The hash of everything that a person approves in one revision of a promise. An approval names it, so the approval binds the content that the approver read. See [Promises and evidence](/reference/promises-and-evidence/#states).

**Critique**: The AI task that asks questions about a design. In the settings, its routing label is **Review**. A critique is not a rule and gives no finding. See [Review a design](/how-to/review-a-design/#ask-the-assistant).

## D

**Decision (ADR)**: A recorded architecture decision with a number such as `ADR-0007`. A person accepts or rejects it. An accepted decision can settle a review finding. See [Record a decision](/how-to/record-a-decision/).

**Declared**: A truth label: a person states that this is so. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/#the-five-labels).

**Disposition**: The answer of a person to one review finding, for example "accepted by a decision" or "won't fix". See [Review rules](/reference/review-rules/).

**Draft**: A copy of the main model in which you make a change that you then merge into main. Also the state of a promise revision that nobody approved. See [Work in a draft](/how-to/work-in-a-draft/) and [Promises and evidence](/reference/promises-and-evidence/#states).

## E

**Element**: A part of the model. The five element kinds are actor, system, app, store and component. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

**Evidence**: A result that Vellum established itself or read from the system that holds it. A statement is not evidence. See [Promises and evidence](/concepts/promises-and-evidence/#what-counts-as-evidence).

## F

**Fact**: One statement of the model that has a truth label: an element, a relationship, a message, an operation, a channel, a subscription or a binding. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

**Finding**: The result of one review rule for one subject of the model. A finding is not proof. See [Findings are not proof](/concepts/findings-are-not-proof/) and [Review rules](/reference/review-rules/).

**Fingerprint**: Of a credential: 8 characters that Vellum computes from the value with a secret key, so that you can check which value is stored. Of a check: the record of the inputs that a run read. See [Configure a workspace](/how-to/configure-settings/#the-fingerprint) and [Promises and evidence](/concepts/promises-and-evidence/#staleness-and-input-fingerprints).

## I

**Inferred**: A truth label: the assistant suggested the fact and a person accepted the suggestion. The fact records a source and a confidence from 0 to 1. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/#the-five-labels).

**Instance administrator**: The account that manages the instance: the policy, the defaults, the master key rotation and the diagnostics. The role gives no access to a workspace. See [Security model](/concepts/security-model/#tenant-isolation).

## M

**Master key**: The 32-byte key from the environment or from a key file under which Vellum encrypts the key of each stored credential. See [Security model](/concepts/security-model/#secrets) and [Rotate the master key](/how-to/rotate-the-master-key/).

**Measured**: A truth label: a test or a run shows the fact, and the fact records a pointer to the evidence. Vellum does not set this label by itself. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/#the-five-labels).

**Message**: A fact of the model: a named payload that one element produces and other elements consume, with an optional schema. See [Model document format](/reference/model-document-format/#message).

## O

**Obligation**: One named check of a promise, with a recipe and an ID such as `ORD-014/O2`. See [Promises and evidence](/reference/promises-and-evidence/#kinds-of-obligation).

**Observed in source**: A truth label: Vellum read the fact in a repository at a commit that the fact records. In the API it is `observed-in-source`. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/#the-five-labels).

**Operation**: A contract that an element owns and other elements call, with the transport `http`, `grpc` or `command`, a timeout and a retry policy. See [Model document format](/reference/model-document-format/#operation).

**Origin (of a credential)**: The scheme, the host and the port of the URL that a credential was saved for, for example `https://api.github.com`. Vellum sends the credential to that origin only. See [Configure a workspace](/how-to/configure-settings/#a-credential-belongs-to-one-host).

## P

**Profile**: The operating mode of an instance: `local` or `hosted`. Also a stress profile: the stated load and capacity for one flow. See [Local and hosted profiles](/concepts/local-and-hosted-profiles/) and [Stress a flow](/how-to/stress-a-flow/).

**Promise**: One statement of behaviour that the system must keep, with a requirement ID such as `ORD-014`. It is a requirement only after an owner approves it. See [Promises and evidence](/concepts/promises-and-evidence/#what-a-promise-is).

**Proposal**: A suggested change from a repository scan, the assistant or an agent. It changes nothing until a person accepts it. See [The model is the source of truth](/concepts/model-is-the-source-of-truth/#proposals-never-write-silently).

**Provenance**: The record of where a fact came from: the source pointer, the revision, the confidence, and who accepted it and when. See [Model document format](/reference/model-document-format/#fact-fields).

## R

**Relationship**: A fact that connects two elements. It describes structure and has no order and no behaviour. See [The model is the source of truth](/concepts/model-is-the-source-of-truth/#a-relationship-is-structure-a-scenario-is-behaviour).

**Revision**: Of a model: a named copy of main at one version, which does not change. Of a promise: one version of its content, which is a draft or approved. See [Compare variants](/how-to/compare-variants/#know-the-three-words) and [Promises and evidence](/reference/promises-and-evidence/#states).

## S

**Scenario**: An ordered list of steps over declared contracts that describes one behaviour of the system. See [Scenario format](/reference/scenario-format/).

**SDD**: The Software Design Document that Vellum generates from one pinned state, as the file `SDD.md` with 12 sections. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#sections-of-sddmd).

**Setup code**: The one-time code that Vellum writes to the log at the first start in the local profile. The first administrator types it in the setup screen. See [Self-host quickstart](/tutorials/self-host-quickstart/#get-the-setup-code).

**Snapshot**: Of a review: the stored findings of one model version with their answers. Of a scan: the stored evidence that the scan read at one commit. See [Review a design](/how-to/review-a-design/#save-a-snapshot) and [Import a repository](/how-to/import-a-repository/).

**Stale**: Of a fact: the last complete scan that looked did not find its evidence. Of a result: an input of the check moved after the run. See [Import a repository](/how-to/import-a-repository/#stale-facts) and [Promises and evidence](/reference/promises-and-evidence/#staleness).

**Store**: An element kind: a place that holds data, such as a database. Its parent is a system. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

**Subscription**: A contract that says an element consumes a channel, with a retry policy and an optional dead-letter channel. See [Model document format](/reference/model-document-format/#subscription).

**System**: An element kind: a software system as a whole. A system has no parent. See [Model a system](/how-to/model-a-system/#know-the-element-kinds).

## T

**Trace**: The replay of a scenario over the contracts of the model. It shows declared behaviour and is not a measurement. See [A trace is declared behaviour](/concepts/traces-are-declared/).

**Tracked ref**: The branch or tag of a repository that the model follows. The first decision in which you accept a proposal sets it. See [Import a repository](/how-to/import-a-repository/#scan-again).

## U

**Unknown**: A truth label: nobody has established the fact. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/#the-five-labels).

## V

**Variant**: A second model that starts as a copy of main or of a revision, for an alternative that you keep next to main. See [Compare variants](/how-to/compare-variants/#know-the-three-words).

**View**: A stored layout of the canvas: the position and the size of each node and the shape of each line. A view has no meaning in the model. See [The model is the source of truth](/concepts/model-is-the-source-of-truth/#the-canvas-is-a-projection).

## W

**Workspace**: The boundary of access. A workspace has members with roles, settings, credentials and projects. See [Security model](/concepts/security-model/#tenant-isolation) and [Manage members](/how-to/manage-members/).
