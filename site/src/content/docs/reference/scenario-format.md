---
title: Scenario format
description: Every field of a scenario document, and the limits of one playback.
---

This page lists the fields of a scenario document.

```json
{
  "id": "<scenario ID>",
  "name": "Place an order",
  "steps": [],
  "faults": [],
  "assertions": []
}
```

All three lists are required. An empty list is valid. A name is required. A create needs an `id`: a UUID that you choose. A replace takes the ID from the path.

A read of one scenario also returns `origin`. It is `null` for a scenario that a person wrote. For an accepted AI flow proposal it holds `kind` (`ai-proposal`), `proposalId`, `provider` and `model`.

## Steps

Every step has a `type` and an `id`. A step ID is unique in the scenario. The `type` can be at any position in the object.

| Type | Fields | What it does |
|---|---|---|
| `call` | `from` (element), `operation`, `request`, `response`, `then`, `onError` | A synchronous request. `then` runs inside the callee before it responds. `onError` runs in the caller after every attempt fails, and handles the failure. |
| `publish` | `from` (element), `channel`, `message`, `payload`, `headers`, `handlers`, `routingKey` (optional) | Publishes a message. Every subscription on the channel receives it. The message also follows the bindings of the channel. `handlers` maps a subscription ID to the steps that consumer runs. |
| `write` | `by` (element), `store` (element), `dataset`, `change` | Writes to a data store. |
| `parallel` | `branches` (a list of step lists) | Starts every branch at the same instant. It ends when the slowest branch ends. |
| `branch` | `whenStep`, `is`, `then`, `else` | Selects a path from the outcome of an earlier step. `is` is `Succeeded`, `Failed` or `Skipped`. `then` runs when the outcome is equal to `is`. `else` runs when it is not. |

A `branch` can read only a step that is final before the branch runs. It cannot read a step of another parallel branch or a step in a consumer handler.

Every step list (`then`, `onError`, `else`, each entry of `branches` and each value of `handlers`) is required. Use an empty list for no steps. `headers` and `handlers` are required. An empty object is valid. `request`, `response`, `payload` and `change` are JSON values or `null`.

## How playback behaves

- Time is virtual. Only a declared timeout and a retry backoff move it.
- A call fails when the callee fails, or when the callee takes longer than the timeout of the operation. A response at the exact end of the timeout is late.
- A failed call is tried again up to the attempts of the operation. Each attempt runs `then` again, so its writes and messages happen again. The trace shows the duplicates.
- A step that fails without `onError` skips the steps after it in the same list.
- A publisher does not wait for its consumers. A consumer failure does not fail the publisher.
- A consumer that fails every attempt sends the message to the dead-letter channel of its subscription. Without one, the message is lost and the trace shows `Dropped`. A fault on the dead-letter channel also gives `Dropped`.
- A published message follows each binding from its channel to the target channel. The subscriptions on the target receive it, and the bindings of the target are followed in the same way. The order is: subscriptions by ID, then bindings by ID.
- A binding without a routing key passes every message. A binding with a routing key passes a message when the `routingKey` of the step is equal to it, or matches it as an AMQP topic pattern. Words are separated by `.`; `*` is one word; `#` is zero or more words. A step without a `routingKey` passes no binding that has one.
- A channel takes one message one time. A cycle of bindings ends.
- A followed binding gives a `Routed` event. A binding that is not followed gives a `NotRouted` event with the reason. For these two kinds, `from` is the source channel, `to` is the target channel and `target` is the binding.
- A fault on a bound channel closes that route only. The trace shows `PublishFailed` for that channel. The other routes continue.
- A dead-lettered message follows the bindings of its dead-letter channel in the same way. It is not delivered to the consumers of those channels: a scenario declares no steps for them. A fault on a channel that such a binding leads to gives `Dropped` for that route.
- The same model revision and scenario always give the same events and the same hash.

## Faults

```json
{ "target": "<ID>", "kind": "Error", "failFirst": 1 }
```

| Field | Values |
|---|---|
| `target` | An operation, a channel, a subscription or an element. A fault on an element fails everything that element serves. |
| `kind` | `Error`, `Outage`, or `Timeout`. `Timeout` is valid only for an operation with a timeout. |
| `failFirst` | The number of attempts that fail, counted in time order. The minimum is 1. `null` fails every attempt. |

One target has at most one fault. A call counts one attempt against a fault on its operation and one attempt against a fault on the element that owns the operation. When both faults apply, the fault on the operation gives the kind. A delivery does the same for its subscription and its consumer.

## Assertions

| Type | Fields | Holds when |
|---|---|---|
| `outcome` | `step`, `expected` | The step has that outcome in the attempt that settled its call or delivery. `expected` is `Succeeded`, `Failed` or `Skipped`. A step with no outcome counts as `Skipped`. |
| `event-count` | `kind`, `target` (or `null`), `expected` | The trace has that many events of the kind. The count includes retried attempts. With a `target`, an event counts only when its `target`, `from` or `to` is that ID. The target is an operation, a channel, a subscription, an element or a binding. `expected` is 0 or more. |

Every assertion has a `type` and an `id`. An assertion ID is unique in the scenario.

Event kinds: `RequestSent`, `ResponseReceived`, `RequestFailed`, `RequestTimedOut`, `RetryScheduled`, `Published`, `PublishFailed`, `Delivered`, `DeliveryFailed`, `DeadLettered`, `Dropped`, `DataWritten`, `DataWriteFailed`, `BranchTaken`, `StepSkipped`, `Routed`, `NotRouted`.

## Validation

Vellum checks a scenario before it plays. A scenario with a problem plays nothing.

| Code | Cause |
|---|---|
| `unknown-element`, `unknown-operation`, `unknown-channel`, `unknown-message`, `unknown-subscription`, `unknown-schema` | The ID is not in the model. |
| `subscription-not-on-channel` | A handler names a subscription that the published channel and its bindings do not reach. |
| `invalid-routing-key` | A routing key is empty, is longer than 200 characters or contains a control character. |
| `unknown-step` | A branch names a step that is not final when the branch runs, or an `outcome` assertion names a step that is not in the scenario. |
| `invalid-outcome` | The `is` of a branch is not `Succeeded`, `Failed` or `Skipped`. |
| `schema-mismatch`, `missing-payload` | A payload does not match the JSON Schema of its operation or message. |
| `invalid-schema` | A schema cannot be evaluated. |
| `duplicate-step-id`, `missing-step-id`, `missing-dataset` | A step ID is used more than one time, a step has no ID, or a write names no dataset. |
| `malformed-scenario` | A required list or map is missing, or a list holds an empty entry. |
| `unknown-fault-target` | A fault target is not an operation, a channel, a subscription or an element. |
| `duplicate-fault` | One target has more than one fault. |
| `invalid-fault` | The kind is not known, a `Timeout` fault is on a target that is not an operation with a timeout, or `failFirst` is less than 1. |
| `invalid-assertion` | An assertion ID is missing or is used more than one time, the expected outcome or the event kind is not known, or the expected count is negative. |
| `unknown-assertion-target` | The `target` of an `event-count` assertion is not an operation, a channel, a subscription, an element or a binding. |
| `invalid-payload` | A payload holds text that is not valid Unicode, holds a number that has more than 40 characters or is outside the range of a 64-bit floating-point number, or nests more than 64 levels. |
| `payload-too-large` | A payload is larger than 64 KB. |
| `too-deep` | The step lists nest more than 24 levels. |
| `too-many-steps` | The scenario has more than 500 steps and step lists. |
| `trace-too-large` | The playback is over the limit of events, of scheduled work items or of bindings for one message. |
| `invalid-model` | The model has a contract that cannot be played: attempts outside 1 to 10, a timeout or a backoff outside 0 to 3,600,000 ms, or a reference to a part that does not exist. |
| `validation-too-slow` | The schema checks took more than 2 seconds. Simplify the patterns in the schemas. |

## Limits

| Limit | Value |
|---|---|
| Nesting depth | 24 |
| Steps and step lists | 500 |
| Attempts of one operation or subscription | 10 |
| Timeout or backoff | 3,600,000 ms |
| Payload size | 64 KB |
| Events in one playback | 10,000 |
| Scheduled work items in one playback | 200,000 |
| Bindings that one message is matched against | 200 |
| Time for all schema checks in one validation | 2 seconds |

## Limits of a saved document

Vellum saves a scenario that has validation problems. It refuses a document that is over one of these limits.

| Limit | Value | Status |
|---|---|---|
| Request body | 1 MiB | 413 |
| Stored document | 512 KiB | 413 with the type `scenario_too_large` |
| Nesting depth of step lists | 24 | 400 |
| Steps and step lists | 500 | 400 |
| Payload size | 64 KB | 400 |
| Arrays and objects nested in one payload | 32 | 400 |
| Scenario name, dataset name, header name, routing key | 200 characters | 400 |
| Step ID, assertion ID, `whenStep`, the `step` of an assertion | 100 characters | 400 |
| Header value | 2,000 characters | 400 |
| Headers of one message | 50 | 400 |
| Scenarios in one project | 2,000 | 409 |

A name, an ID, a routing key or a header must not contain a control character or text that is not valid Unicode. A payload must not contain the character U+0000 or text that is not valid Unicode. A number in a payload has at most 40 characters and a finite 64-bit floating-point value.

A document that breaks a limit gets `400` with the type `validation_error` and the path of the value. A body that is not a scenario document gets `400` with the type `invalid_scenario`. This includes a step or an assertion that has no `type`, or a `type` that is not in the tables on this page. A create with an ID that is in use gets `409`. A deleted scenario keeps its ID.

## HTTP API

| Request | Role | Result |
|---|---|---|
| `GET /api/projects/{projectId}/scenarios` | Viewer | One page of the scenarios of the project, in name order. Each item has `id` and `name`. `limit` is 1 to 200. The default is 50. Send `nextCursor` as `cursor` to get the next page. |
| `POST /api/projects/{projectId}/scenarios` | Editor | Creates a scenario. The body is the document. The answer is `201`. |
| `GET /api/projects/{projectId}/scenarios/{scenarioId}` | Viewer | Reads one scenario. |
| `PUT /api/projects/{projectId}/scenarios/{scenarioId}` | Editor | Replaces one scenario. The body can leave the `id` out. It cannot name a different ID. |
| `DELETE /api/projects/{projectId}/scenarios/{scenarioId}` | Editor | Deletes one scenario. The answer is `204`. |
| `POST /api/projects/{projectId}/scenarios/{scenarioId}/run` | Viewer | Plays the scenario against the branch that the query parameter `branchId` names. Without `branchId`, it plays against the main model. It writes nothing. An invalid scenario returns `200` with `issues` and no events. |

An agent token with the `read` scope can list, read and run scenarios. It cannot create, replace or delete one.

A run returns `engineVersion`, `hash`, `modelVersion`, `issues`, `events`, `outcomes`, `assertions` and `labels`. The engine version is `1`. `labels` gives a name for each ID that an event can hold. Each issue has `stepId`, `code` and `message`. Each event has `sequence`, `atMs`, `kind`, `stepId`, `attempt`, `from`, `to`, `target`, `payload`, `detail`, `message` and `headers`. Each assertion result has `id`, `passed` and `detail`.

## Related pages

- [Trace a scenario](/how-to/trace-a-scenario/)
- [A trace is declared behaviour](/concepts/traces-are-declared/)
- [Model document format](/reference/model-document-format/)
- [HTTP API](/reference/http-api/)
