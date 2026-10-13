---
title: Trace a scenario
description: Declare contracts, write a scenario and play it on the canvas.
---

This guide shows you how to play one behaviour of your system on the canvas. You need the editor role to add a contract and to save a scenario. A viewer can play a saved scenario.

A trace replays what you declared. It does not call or observe your system. See [A trace is declared behaviour](/concepts/traces-are-declared/).

## Declare the contracts

A scenario can reference only contracts that exist in the model. A contract names elements, so add the elements first. See [Model a system](/how-to/model-a-system/).

1. Open **Contracts** in the left toolbar.
2. In **Operations**, select **Add** for each call a service accepts. Give **Name**, **Offered by**, **Transport**, **Timeout (ms)**, **Attempts** and **Backoff (ms)**. Select **Add operation**.
3. In **Channels**, select **Add** for each topic, queue or exchange. Give **Name** and **Kind**. Select **Add channel**.
4. In **Subscriptions**, select **Add** for each consumer of a channel. Give **Channel**, **Consumer**, **Attempts** and **Backoff (ms)**.
5. Select a **Dead-letter channel** for the subscription. Without one, a message that fails every attempt is lost. Select **Add subscription**.
6. When a producer publishes to an exchange or a topic and a consumer reads a queue, select **Bind to a channel** below the exchange or the topic. The button reads **Add binding** when the channel has a binding. In **Route to**, select the queue or the exchange to route to. Give **Routing key (optional)** only when the broker uses one. Select **Add binding**.

**Attempts** is a number from 1 to 10. A binding starts at an exchange or a topic and ends at a queue or an exchange.

A scenario then publishes to the exchange, as the code does. The trace shows each binding that the message follows and each binding that it does not follow.

Vellum refuses to remove an element, a channel or a schema that a contract uses.

## Write the scenario

1. Open **Scenarios** in the left toolbar.
2. Select **New**.
3. Give the scenario a name in **Name**.
4. Write the steps in **Document**. Select an entry in the reference list to copy its ID. The group **Handlers** lists, for each channel, the subscriptions that a publish to that channel can reach.
5. Select **Save and play**. **Save** saves the scenario and does not play it.

The [scenario format](/reference/scenario-format/) lists every step type and every limit. **Format reference** in the editor shows a short form of it.

To play a saved scenario, select the play button on its row in **Scenarios**. To change it, select the edit button on its row.

When a draft is open, the scenario plays against the contracts of the draft.

## Play it

The dock at the bottom of the canvas controls playback.

- **Play** moves through the events in order. **Pause** stops. Select a speed: 0.5×, 1×, 2× or 4×.
- **Previous event** and **Next event** move one event. **Restart** goes back to the moment before the first event.
- Select a tick on the timeline to go to that event.
- The line below the timeline states the event in words. The box on the right shows its payload.
- The canvas shows a ring on the elements that take part, and an edge between them.
- **Close trace** closes the dock.

Each colour has one meaning: teal is a request, violet is a message, cyan is a data write, amber is a retry, red is a failure. Grey is a branch, a skipped step or a binding that the message does not follow. A failure tick is also taller, and a retry tick is thinner. With reduced motion on, the moving dot is hidden and nothing else changes.

The dock always shows the line "Declared behaviour, not a measurement".

## Read the result

Select the expectation count in the dock to see each expectation and why it holds or fails.

If the scenario has an invalid reference or payload, the dock says "This scenario cannot play", lists each problem and plays nothing.

## Inject a failure

Add a fault to the `faults` list of the scenario:

```json
{ "target": "<operation, channel, subscription or element ID>", "kind": "Error", "failFirst": 1 }
```

`kind` is `Error`, `Timeout` or `Outage`. `failFirst` is the number of attempts that fail. `null` fails every attempt. `Timeout` is valid only for an operation that declares a timeout. One target takes one fault.

## Limits

- A message that moves to a dead-letter channel follows the bindings of that channel. The trace does not deliver it to the consumers of the dead-letter channel.
- The trace check does not check the kind of an element. For example, it accepts a write step whose `store` is an actor.
- The trace check does not check that the element that publishes a message is the declared producer of that message.
- The [scenario format](/reference/scenario-format/#limits) gives the limits of one playback.

## See also

- [First model and trace](/tutorials/first-model-and-trace/)
- [Model a system](/how-to/model-a-system/)
- [Design with the assistant](/how-to/design-with-the-assistant/)
- [Scenario format](/reference/scenario-format/)
- [A trace is declared behaviour](/concepts/traces-are-declared/)
