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

All three lists are required. An empty list is valid.

## Steps

Every step has a `type` and an `id`. A step ID is unique in the scenario. The `type` can be at any position in the object.

| Type | Fields | What it does |
|---|---|---|
| `call` | `from` (element), `operation`, `request`, `response`, `then`, `onError` | A synchronous request. `then` runs inside the callee before it responds. `onError` runs in the caller after every attempt fails, and handles the failure. |
| `publish` | `from` (element), `channel`, `message`, `payload`, `headers`, `handlers`, `routingKey` (optional) | Publishes a message. Every subscription on the channel receives it. The message also follows the bindings of the channel. `handlers` maps a subscription ID to the steps that consumer runs. |
| `write` | `by` (element), `store` (element), `dataset`, `change` | Writes to a data store. |
| `parallel` | `branches` (a list of step lists) | Starts every branch at the same instant. It ends when the slowest branch ends. |
| `branch` | `whenStep`, `is`, `then`, `else` | Selects a path from the outcome of an earlier step. `is` is `Succeeded`, `Failed` or `Skipped`. |

A `branch` can read only a step that is final before the branch runs. It cannot read a step of another parallel branch or a step in a consumer handler.

## How playback behaves

- Time is virtual. Only a declared timeout and a retry backoff move it.
- A call fails when the callee fails, or when the callee takes longer than the timeout of the operation. A response at the exact end of the timeout is late.
- A failed call is tried again up to the attempts of the operation. Each attempt runs `then` again, so its writes and messages happen again. The trace shows the duplicates.
- A step that fails without `onError` skips the steps after it in the same list.
- A publisher does not wait for its consumers. A consumer failure does not fail the publisher.
- A consumer that fails every attempt sends the message to the dead-letter channel of its subscription. Without one, the message is lost.
- A published message follows each binding from its channel to the target channel. The subscriptions on the target receive it, and the bindings of the target are followed in the same way. The order is: subscriptions by ID, then bindings by ID.
- A binding without a routing key passes every message. A binding with a routing key passes a message when the `routingKey` of the step is equal to it, or matches it as an AMQP topic pattern. Words are separated by `.`; `*` is one word; `#` is zero or more words. A step without a `routingKey` passes no binding that has one.
- A channel takes one message one time. A cycle of bindings ends.
- A followed binding gives a `Routed` event. A binding that is not followed gives a `NotRouted` event with the reason. For these two kinds, `from` is the source channel, `to` is the target channel and `target` is the binding.
- A fault on a bound channel closes that route only. The trace shows `PublishFailed` for that channel. The other routes continue.
- A dead-lettered message follows the bindings of its dead-letter channel in the same way. It is not delivered to the consumers of those channels: a scenario declares no steps for them.
- The same model revision and scenario always give the same events and the same hash.

## Faults

```json
{ "target": "<ID>", "kind": "Error", "failFirst": 1 }
```

| Field | Values |
|---|---|
| `target` | An operation, a channel, a subscription or an element. A fault on an element fails everything that element serves. |
| `kind` | `Error`, `Outage`, or `Timeout`. `Timeout` is valid only for an operation with a timeout. |
| `failFirst` | The number of attempts that fail, counted in time order. `null` fails every attempt. |

One target has at most one fault.

## Assertions

| Type | Fields | Holds when |
|---|---|---|
| `outcome` | `step`, `expected` | The step has that outcome in the attempt that settled its call or delivery. |
| `event-count` | `kind`, `target` (or `null`), `expected` | The trace has that many events of the kind. The count includes retried attempts. |

Event kinds: `RequestSent`, `ResponseReceived`, `RequestFailed`, `RequestTimedOut`, `RetryScheduled`, `Published`, `PublishFailed`, `Delivered`, `DeliveryFailed`, `DeadLettered`, `Dropped`, `DataWritten`, `DataWriteFailed`, `BranchTaken`, `StepSkipped`, `Routed`, `NotRouted`.

## Validation

Vellum checks a scenario before it plays. A scenario with a problem plays nothing.

| Code | Cause |
|---|---|
| `unknown-element`, `unknown-operation`, `unknown-channel`, `unknown-message`, `unknown-subscription`, `unknown-schema` | The ID is not in the model. |
| `subscription-not-on-channel` | A handler names a subscription that the published channel and its bindings do not reach. |
| `invalid-routing-key` | A routing key is empty, is longer than 200 characters or contains a control character. |
| `unknown-step` | A branch or an assertion names a step that does not exist, or that is not final in time. |
| `schema-mismatch`, `missing-payload` | A payload does not match the JSON Schema of its operation or message. |
| `invalid-schema` | A schema cannot be evaluated. |
| `duplicate-step-id`, `missing-step-id`, `missing-dataset`, `malformed-scenario` | The document is incomplete. |
| `invalid-fault`, `duplicate-fault`, `unknown-fault-target`, `invalid-assertion`, `unknown-assertion-target` | A fault or an assertion is not valid. |
| `invalid-payload`, `payload-too-large` | A payload is not valid JSON text, or is larger than 64 KB. |
| `too-deep`, `too-many-steps`, `trace-too-large` | The scenario is over a limit. |
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
| Bindings that one message is matched against | 200 |

## Limits of a saved document

Vellum saves a scenario that has validation problems. It refuses a document that is over one of these limits.

| Limit | Value | Status |
|---|---|---|
| Request body | 1 MiB | 413 |
| Stored document | 512 KiB | 413 |
| Nesting depth of step lists | 24 | 400 |
| Steps and step lists | 500 | 400 |
| Payload size | 64 KB | 400 |
| Arrays and objects nested in one payload | 32 | 400 |
| Scenario name, dataset name, header name, routing key | 200 characters | 400 |
| Step ID, assertion ID | 100 characters | 400 |
| Header value | 2,000 characters | 400 |
| Headers of one message | 50 | 400 |
| Scenarios in one project | 2,000 | 409 |

A name, an ID, a routing key or a header must not contain a control character. A payload must not contain the character U+0000.

## HTTP API

| Request | Result |
|---|---|
| `GET /api/projects/{projectId}/scenarios` | The scenarios of the project. |
| `POST /api/projects/{projectId}/scenarios` | Creates a scenario. The body is the document. |
| `GET`, `PUT`, `DELETE /api/projects/{projectId}/scenarios/{id}` | Reads, replaces or deletes one scenario. |
| `POST /api/projects/{projectId}/scenarios/{id}/run?branchId=` | Plays the scenario against the branch. It writes nothing. An invalid scenario returns `200` with `issues` and no events. |
