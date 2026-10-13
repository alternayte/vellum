---
title: Review rules
description: Every rule of the review, what it reads, each result it gives, its severity and how to settle it.
---

This page lists the rules of the review. The rule set has the version `review-rules/2`. Every rule has an identifier and a version. The rules REL-003, CON-002, CON-004 and TRU-001 have version 1. Each other rule on this page has version 2.

An answer that a person gave to a finding of a version 1 rule shows `needs re-review` when the rule has version 2 now. The API gives this as `needsReReview: true`. Read the finding again and settle it again.

A rule is a function of one pinned state: the model, the scenarios, the schemas and the promises. It calls no AI and reads no clock. The same state gives the same findings and the same hash.

A rule does not read the links between the model and an import. It reads the truth label and the stale mark that an import left on a fact.

To learn what a finding establishes, see [Findings are not proof](/concepts/findings-are-not-proof/). To work with the findings, see [Review a design](/how-to/review-a-design/).

## Endpoints

| Endpoint | What it gives | Who |
|---|---|---|
| `GET /api/projects/{projectId}/review/rules` | Each rule: `id`, `version`, `category`, `title`, `reads`, `settle`. | A person, signed in. An agent token with the scope `read`. |
| `GET /api/projects/{projectId}/review/findings` | The findings of one pinned state, with `ruleSetVersion`, `hash`, `inputsFingerprint` and `counts`. | A viewer. An agent token with the scope `read`. |
| `PUT /api/projects/{projectId}/review/dispositions` | Sets the answer to one finding. See "Answers". | A person, signed in. |
| `GET /api/projects/{projectId}/review/snapshots` | The saved snapshots of the findings. | A viewer, signed in. |
| `POST /api/projects/{projectId}/review/snapshots` | Saves the findings of a pinned state with their answers. | An editor, signed in. |
| `GET /api/projects/{projectId}/review/snapshots/{snapshotId}` | One saved snapshot. | A viewer, signed in. |
| `DELETE /api/projects/{projectId}/review/snapshots/{snapshotId}` | Deletes one saved snapshot. | An editor, signed in. |

The findings endpoint reads the main model. The query parameter `revisionId` names a revision of the model. The query parameter `branchId` names a draft or a variant. The scenarios, the schemas, the promises and the decisions are read as they are now.

The `category` of a rule is `resilience`, `contracts`, `truth` or `overengineering`.

The `counts` are `blocking`, `concerns`, `unknown`, `verified`, `notApplicable`, `settled` and `needsReReview`.

The findings endpoint gives 413 `review_too_large` when the pinned state is larger than the limits of an export.

## Results

A rule gives one result for each subject.

| Result | Meaning |
|---|---|
| `verified` | The declared model satisfies the rule. It says nothing about a running system. |
| `concern` | The declared model does not satisfy the rule. |
| `unknown` | The rule cannot read a fact that it needs. |
| `not-applicable` | The rule does not read this subject. |

## Facts that a rule does not read

A rule reads a fact as stated only when its truth label is `declared`, `observed-in-source` or `measured`, and it is not stale.

A fact that is `inferred`, that has the label `unknown`, or that is marked stale gives the result `unknown`. So does an ID that the model does not hold. The rule gives neither `verified` nor `concern` for it. The finding names the fact. Confirm the fact or correct it.

This applies to every rule below that lists "a fact in doubt". The rule TRU-001 is different: it reports such a fact as a concern.

A rule cites every fact that its result rests on. Each cited fact passes this check before the rule gives `verified` or `concern`. So a `verified` finding never cites a fact in doubt.

## Severity and Build Ready

| Severity | Meaning |
|---|---|
| `blocking` | A concern prevents Build Ready. |
| `warning` | The finding asks for an answer. It blocks nothing. |
| `note` | The finding gives numbers or a hint. It blocks nothing. |

Only a finding with the severity `blocking` and the result `concern` blocks Build Ready. It adds the blocker `review-<rule ID>` to the Build manifest. An `unknown` never blocks. No finding prevents a save. No finding prevents an export: the manifest of the bundle says `buildReady: false` and lists the blockers. See [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/).

A rule gives the severity `blocking` only on the path of an approved promise. The path of a promise holds:

- The operations, channels, subscriptions, messages and elements that the approved revision links.
- The scenarios that it links, and every part that a step of those scenarios names: the caller, the operation, the publisher, the channel, the message, the handler subscriptions, the writer and the store.
- What each of those parts hangs from: the owner of an operation, the channel, the consumer and the dead-letter channel of a subscription, the producer of a message, the subscriptions on a channel, and the bindings that leave a channel with their target channels.
- What each added part hangs from, by the same list, until the path holds nothing new. So the path holds a subscription behind a binding of a linked channel, and the consumer of that subscription.

## Answers

| Answer | Who | Effect |
|---|---|---|
| `accepted-by-decision` | An owner, signed in | An accepted decision in force that names the rule and the subject settles the finding. A settled blocking finding does not block. |
| `wont-fix` | An editor, signed in | Needs a reason. It is refused for a blocking finding, and it never removes a blocker. |
| `open` | An editor. An owner when a decision names the finding | Removes the answer. |

An answer records the version of the rule and the fingerprint of the finding. The fingerprint is a hash of the result, the severity, the cited IDs and the reason. Where the reason gives a count or names only the first item, the fingerprint also holds each item: each refused payload with its step and its code (CON-003), each shared dataset with its writers (CON-005), each operation with no timeout (REL-002), and each obligation with its result and each gap (TRU-002). When either differs later, the finding shows `needs re-review` and the answer settles nothing. A blocking finding then blocks again.

A decision settles a finding only while it is accepted and no accepted decision supersedes it. A proposed, rejected or superseded decision settles nothing.

A settled finding is not deleted. The list and the SDD show it with its decision.

## Resilience

### REL-001 A failed delivery has somewhere to go

Reads each subscription: its channel, its consumer, its retry attempts and its dead-letter channel.

| Result | When |
|---|---|
| `concern` | The subscription has one delivery attempt and no dead-letter channel. A message whose delivery fails is lost. |
| `verified` | The subscription has more than one attempt, or a dead-letter channel. |
| `unknown` | The subscription, its channel, its consumer or its dead-letter channel is a fact in doubt. |

Severity: `warning`. To settle: give the subscription more than one attempt or a dead-letter channel, or accept the loss in a decision.

### REL-002 A chain of synchronous calls has a timeout on every hop

Reads each scenario: the call steps that run inside another call, and the timeout of each operation in the chain. A chain is a call and the calls that its callee makes before it answers. The steps of `onError` run after the call ended. A handler of a published message starts a new chain.

A timeout of 0 declares none. On an operation with the label `declared`, that is the statement "no timeout". On an operation with another label, an import read no timeout, which is no statement.

| Result | When |
|---|---|
| `not-applicable` | The scenario has no call that runs inside another call. |
| `concern` | A chain of two calls or more holds a declared operation with the timeout 0. |
| `unknown` | An operation of a chain is a fact in doubt. Or each operation with the timeout 0 is not declared. |
| `verified` | Every operation in the chains declares a timeout. |

Severity: `warning`. To settle: declare a timeout on each operation of the chain, or record why the chain may wait without limit.

### REL-003 A retried operation is safe to repeat

Reads each operation: its retry attempts. The model holds no idempotency fact for an operation. So this rule never gives `verified` and never gives `concern`.

| Result | When |
|---|---|
| `unknown` | The operation has more than one attempt. |
| `not-applicable` | The operation declares no retry. |

Severity: `warning`. To settle: record in a decision how a repeated call is made harmless, or remove the retry.

### REL-004 A published message can reach a subscription

Reads each channel that a scenario publishes to: a topic, an exchange or a queue. Reads its bindings, the channels behind them, and the subscriptions on it and behind them. Routing keys are not read here. See REL-006.

| Result | When |
|---|---|
| `not-applicable` | No scenario publishes to the channel. |
| `concern` | No subscription is on the channel or behind its bindings. This applies to a queue too: the queue keeps the message, and the model shows nothing that takes it. |
| `verified` | A subscription can be reached from the channel. |
| `unknown` | The channel, a binding behind it, a channel behind it or a subscription behind it is a fact in doubt. |

Severity: `blocking` when an approved promise links a scenario that publishes to the channel. Otherwise `warning`. To settle: add a subscription or a binding that leads to one, or remove the publish step.

### REL-005 A dead-letter channel has a consumer

Reads each channel that a subscription names as its dead-letter channel, those subscriptions, and the subscriptions on it and behind its bindings.

| Result | When |
|---|---|
| `not-applicable` | No subscription dead-letters to the channel. |
| `concern` | Nothing in the model consumes the dead-letter channel. |
| `verified` | A subscription can consume it. |
| `unknown` | The channel, a subscription that names it, or a binding, a channel or a subscription behind it is a fact in doubt. |

Severity: `note`. To settle: add a subscription to the dead-letter channel, or record who reads it outside the model.

### REL-006 A binding with a routing key passes a published message

Reads each binding with a routing key, and the routing keys of the scenario steps that publish to its source or to a channel that leads to its source. A key matches when it is equal, or when it matches as an AMQP topic pattern: `*` is one word and `#` is zero or more words.

| Result | When |
|---|---|
| `not-applicable` | The binding has no routing key. It passes every message. |
| `verified` | A publish step states a routing key that the binding matches. |
| `concern` | Publish steps state routing keys, and none matches the binding. |
| `unknown` | No scenario publishes to the source, or the publish steps state no routing key. Or the binding, one of its channels, or a channel or a binding between a publish step and its source is a fact in doubt. |

Severity: `warning`. To settle: state the routing key on a publish step, or correct the key of the binding.

## Contracts

### CON-001 An operation and a message have a schema

Reads each operation (its owner, its request and response schema) and each message (its producer and its schema).

| Result | When |
|---|---|
| `concern` | An operation has no request schema and no response schema. A message has no schema. |
| `verified` | The operation names a request schema or a response schema. The message names a schema. |
| `unknown` | The operation or its owner is a fact in doubt. The message or its producer is a fact in doubt. |

Severity: `blocking` on the path of an approved promise. Otherwise `warning`. To settle: give the operation or the message a schema, or settle the finding with an accepted decision.

### CON-002 A referenced schema exists

Reads each operation and message: the schema IDs it names, against the schemas of the project.

| Result | When |
|---|---|
| `not-applicable` | The operation or the message names no schema. |
| `concern` | A named schema does not exist in the project. |
| `verified` | Every named schema exists. |
| `unknown` | The operation or the message names a schema and is a fact in doubt. |

Severity: `blocking` on the path of an approved promise. Otherwise `warning`. To settle: create the schema again, or point the contract at a schema that exists.

### CON-003 A scenario payload is valid against its schema

Reads each scenario through the trace validator: the operation of every call step and the message of every publish step, and every request, response and message payload against the schema of that contract. The finding names the first refused payload and gives the count. Its fingerprint holds each of them.

| Result | When |
|---|---|
| `unknown` | The operation of a call step or the message of a publish step is a fact in doubt. This comes first: the schema of a payload is the one that such a contract names. |
| `concern` | The validator refuses a payload or its schema (`schema-mismatch`, `invalid-payload`, `invalid-schema`, `unknown-schema`, `missing-payload`, `payload-too-large`). |
| `unknown` | The validator reports another problem, so the payloads were not all checked. Or no step has a payload with a schema to validate against. |
| `verified` | Every payload is valid against its schema, and at least one schema was read. |

Severity: `blocking` when an approved promise links the scenario. Otherwise `warning`. To settle: correct the payload or the schema.

### CON-004 A scenario shows the producer publishing its message

Reads each message: its producer, and the publish steps of every scenario. This rule never gives `concern`. A scenario that publishes the message from another element is the Build blocker `producer-mismatch`.

| Result | When |
|---|---|
| `verified` | A scenario shows the producer publishing the message. |
| `unknown` | No scenario shows that. Or the message or its producer is a fact in doubt. |

Severity: `warning`. To settle: add a scenario in which the producer publishes the message.

### CON-005 One element writes a dataset

Reads each store: the write steps of every scenario, by dataset and writer. The finding names the first shared dataset and gives the count of the others. Its fingerprint holds each shared dataset with its writers.

| Result | When |
|---|---|
| `concern` | Two elements write the same dataset of the store. They are coupled through its data. |
| `verified` | Each dataset that scenarios write has one writer. |
| `unknown` | No scenario writes to the store. Or the store or a writer is a fact in doubt. |

Severity: `warning`. To settle: give the dataset one writer, or record in a decision why two elements share it.

## Truth

### TRU-001 An approved promise stands on confirmed facts

Reads each fact on the path of an approved promise: its truth label and its stale mark. A fact that is not on such a path gets no finding of this rule. The path is described under "Severity and Build Ready". It holds the subscriptions on a linked channel and behind its bindings.

| Result | When |
|---|---|
| `concern` | The fact is inferred, has the label `unknown`, or is marked stale. |
| `verified` | The fact is declared, observed in source or measured, and is not stale. |

Severity: `blocking`. To settle: confirm the fact or remove it.

An accepted decision settles the finding and removes the blocker `review-TRU-001`. It does not change the label. The Build blockers `fact-inferred`, `fact-unknown` and `fact-stale` stay until a person confirms the fact.

### TRU-002 A promise in force has current evidence

Reads each promise: the evidence status that the Promises module computes for the revision in force. The rule does not compute it again.

| Result | When |
|---|---|
| `not-applicable` | The promise is a draft or is retired. |
| `concern` | The promise has no obligation. Or an obligation failed its newest check, has a result for inputs that moved, or has no result. Or the status lists a gap. |
| `verified` | Every obligation has a result that passed on the pinned inputs. Each result covers only what its check names. |

Severity: `warning`. The Build module has its own blockers for the evidence of a promise. To settle: run the checks on the pinned inputs, or record evidence for the commit the model follows.

## Overengineering

These rules argue against a service, a queue or a part that the model gives no reason for. A concern of these rules is a prompt for a decision. Each concern holds a question. Change the model, or answer the question in a decision and accept it. An accepted decision that names the rule and the subject settles the finding.

### OVR-001 An app does more than pass a call on

Reads each app: the scenario steps that call it, that it calls, publishes and writes, the operations, channels, messages and stores that those steps name, and the subscriptions that it consumes.

| Result | When |
|---|---|
| `concern` | In the scenarios the app has one synchronous caller and one callee, and it publishes, consumes and writes nothing. |
| `verified` | The app has a second caller or callee, or it publishes, consumes or writes. |
| `unknown` | No scenario shows traffic of the app. Or the app, a caller, a callee, an operation of those calls, a subscription that it consumes, or a channel, a message or a store that its steps name is a fact in doubt. |

Severity: `warning`. Question: "What does the app add between its caller and its callee?"

### OVR-002 An asynchronous hop inside one system has a stated benefit

Reads each channel: the scenario steps that publish to it, the one subscription behind it and the bindings that lead to it, the parent system of the publisher and the consumer, the retry and dead-letter channel of the subscription, and synchronous calls between the two in the same scenario.

| Result | When |
|---|---|
| `not-applicable` | The channel does not have exactly one subscription behind it. Or the publisher and the consumer are not two apps of one system. |
| `concern` | The same scenario also has a synchronous call between the two apps. Or the subscription has one attempt and no dead-letter channel. |
| `verified` | The subscription declares a retry or a dead-letter channel, and no scenario also calls between the two synchronously. |
| `unknown` | No scenario publishes to the channel. Or the channel, the subscription, its dead-letter channel, the consumer, a publisher, the parent system, a binding or a channel on the way, or the operation of such a synchronous call is a fact in doubt. |

Severity: `warning`. Question: "What does the queue buy here?"

### OVR-003 A system has no more apps than its data and its actors explain

Reads each system: the apps and the stores it holds, and the actors that call it in a scenario or have a relationship with it. The finding gives the three numbers.

| Result | When |
|---|---|
| `not-applicable` | The system holds no app. |
| `concern` | The system holds more apps than stores and actors together. |
| `verified` | The system holds at most as many apps as stores and actors together. |
| `unknown` | The system, one of its children, an actor that the rule counts, or the operation or the relationship that makes the actor a user is a fact in doubt. |

Severity: `note`. Question: "What does each app own that another app cannot?"

### OVR-004 A channel has a subscription

Reads each channel: the subscriptions on it and behind its bindings, with those bindings and the channels behind them.

| Result | When |
|---|---|
| `concern` | No subscription is on the channel or behind its bindings. |
| `verified` | A subscription can be reached from the channel. |
| `unknown` | The channel, a binding behind it, a channel behind it or a subscription behind it is a fact in doubt. |

Severity: `warning`. Question: "Who reads the channel?"

### OVR-005 An element is used

Reads each element: its operations, subscriptions, messages, relationships and children, and the scenario steps that name it. One of them that is confirmed is enough. A scenario step counts as confirmed: a person wrote it.

| Result | When |
|---|---|
| `concern` | The element has no operation, subscription, message, relationship or child, and no scenario step names it. |
| `verified` | A contract, a relationship or a child that is not a fact in doubt names the element. Or a scenario step names it. The finding cites one of them. |
| `unknown` | The element is a fact in doubt. Or each contract, relationship and child that names it is a fact in doubt, and no scenario step names it. |

Severity: `warning`. Question: "What is the element for?"

## What the assistant may say

The critique of the assistant is not a rule and gives no finding. Vellum checks each item of its answer on the server:

- An item that names a part outside the tables of the request is left out.
- A risk is shown as a risk only when its basis is a rule that gave a concern or an unknown for a part it names. Every other risk is shown as a question, with the note that the model does not show it.
- Vellum checks that the rule fired. It does not check what the sentence of the risk says. So a risk shows two sentences: the sentence of the finding, as the rule gave it, and the sentence of the assistant, with that label. The API gives them as `findingReason` and `claim`, with `findingResult` and `claimSource: assistant`.
- A proposed decision keeps the rule it would settle only for the findings that the rule gave for the parts it names.
- An item that holds one of these words is left out: `verified`, `proven`, `safe`, `secure`, `scalable`, `compliant`, `guaranteed`, `resolved`. The assistant cannot establish that. Vellum cannot tell a negation from an assertion reliably, so the word alone decides.

The answer lists what was left out, with the reason.

No import writes a scenario or a promise. A person, an agent or the assistant writes them, and their text can hold the name of a part that came from a private repository. While the setting `privacy.sendPrivateCodeToAi` is off, the prompt leaves out each scenario whose name holds such a name, each promise whose requirement ID or title holds one, and each finding whose sentence or question holds one. The answer gives the count in `omittedPrivate` and `findingsLeftOut`.
