# Chat service example

The template **Chat service** of an empty project. A member sends a message: the chat
gateway takes it, the Messages service stores it and publishes `MessageSent`, and the
Notifier sends a push notification through an external push service.

## Files

| File | Content |
|---|---|
| `model.json` | A Vellum model document: 8 elements, 7 relationships, 5 JSON schemas, 3 operations, 3 channels, 1 binding, 1 subscription and 1 message. |
| `scenarios/send-a-message.json` | The flow with no fault. |
| `scenarios/send-a-message-push-service-down.json` | The same flow with an outage of `POST /push`. The message is still stored, and the delivery goes to the dead-letter queue. |
| `stress-profile.json` | The stress profile **Evening peak** of `Send a message`: 120 messages each second, with a burst of 600 each second for 15 seconds. Every number is declared. |
| `build.mjs` | Builds the model through the HTTP API and writes the files above, with `../build-kit.mjs`. Maintainers use it. |

## What the model holds

- **Elements:** the actor `Member`; the system `Chat` with `Chat gateway`, `Messages service`,
  `Messages DB`, `Presence cache` and `Notifier`; the external system `Push service`.
- **Operations:** `POST /messages` (Chat gateway), `StoreMessage` (Messages service, gRPC,
  2 attempts), `POST /push` (Push service, 2 attempts).
- **Channels:** the topic `message-events`, the queue `notifier.message-sent` with a binding
  on `message.sent`, and the dead-letter queue `message-events.dlq`.
- **Subscription:** the Notifier on its queue, with 3 attempts and the dead-letter queue.

Every fact has the truth label `declared`.

## Keep it working

`../orders/README.md` describes the test `ExampleTests` and how to build the files again.
