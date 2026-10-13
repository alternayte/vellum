---
title: Trace a scenario
description: Declare contracts, write a scenario and play it on the canvas.
---

This guide shows you how to play one behaviour of your system on the canvas.

A trace replays what you declared. It does not call or observe your system.

## Declare the contracts

A scenario can reference only contracts that exist in the model.

1. Open **Contracts** in the left toolbar.
2. Add an **operation** for each call a service accepts. Give its timeout and its retry attempts.
3. Add a **channel** for each topic, queue or exchange.
4. Add a **subscription** for each consumer of a channel. Give its retry attempts.
5. Select a dead-letter channel for the subscription. Without one, a message that fails every attempt is lost.
6. When a producer publishes to an exchange or a topic and a consumer reads a queue, add a **binding** below the exchange or the topic. Select the channel to route to. Give a routing key only when the broker uses one.

A scenario then publishes to the exchange, as the code does. The trace shows each binding that the message follows and each binding that it does not follow.

Vellum refuses to remove an element, a channel or a schema that a contract uses.

## Write the scenario

1. Open **Scenarios** in the left toolbar.
2. Select **New**.
3. Give the scenario a name.
4. Write the steps in the document. Select an entry in the reference list to copy its ID.
5. Select **Save and play**.

The [scenario format](/reference/scenario-format/) lists every step type.

## Play it

The dock at the bottom of the canvas controls playback.

- **Play** moves through the events in order. Select a speed from 0.5× to 4×.
- **Previous** and **Next** move one event.
- Select a tick on the timeline to go to that event.
- The line below the timeline states the event in words. The box on the right shows its payload.
- The canvas shows a ring on the elements that take part, and an edge between them.

Each colour has one meaning: teal is a request, violet is a message, cyan is a data write, amber is a retry, red is a failure. A failure tick is also taller, and a retry tick is thinner. With reduced motion on, the moving dot is hidden and nothing else changes.

## Read the result

Select the expectation count in the dock to see each expectation and why it holds or fails.

If the scenario has an invalid reference or payload, the dock lists each problem and plays nothing.

## Inject a failure

Add a fault to the `faults` list of the scenario:

```json
{ "target": "<operation, channel, subscription or element ID>", "kind": "Error", "failFirst": 1 }
```

`failFirst` is the number of attempts that fail. `null` fails every attempt. `Timeout` is valid only for an operation that declares a timeout.
