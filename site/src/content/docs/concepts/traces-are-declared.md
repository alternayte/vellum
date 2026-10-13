---
title: A trace is declared behaviour
description: What a trace shows, and what it cannot show.
---

This page explains what a trace is evidence of.

A trace replays a scenario over the contracts in the model. It reads no code and calls no service. It shows what the declared design does when each part behaves as declared, and when a part fails as you tell it to.

Time in a trace is virtual. Only a declared timeout and a declared retry backoff advance it. The engine reads no clock and no random source, so the same scenario on the same model gives the same trace.

A trace can show:

- The order of calls, writes and messages for one behaviour.
- What a declared timeout and a declared retry do to that order.
- Where a write or a message happens again after a retry. The trace shows each repeat as an event of its own. It does not mark the repeat as a duplicate.
- Where a message is lost because no dead-letter channel is declared. The trace shows a `Dropped` event.
- Which bindings a published message follows, and which it does not follow, with the reason.
- A scenario that references a contract that does not exist. Vellum reports this as a validation issue.

A trace cannot show:

- That the running system has these contracts.
- Real latency, throughput or failure rates. A time in a trace is a sum of declared timeouts and backoffs, not a duration that somebody measured.
- That a retry in the code is safe.

The dock states this on every playback: **Declared behaviour, not a measurement**. Evidence from source code and from test runs has its own labels in Vellum, and a trace never changes them.

A `scenario-check` of a promise is a trace with assertions. A pass shows that the declared design keeps the promise. It does not show that the code does. See [Promises and evidence](/concepts/promises-and-evidence/).

## Related pages

- [Trace a scenario](/how-to/trace-a-scenario/)
- [Scenario format](/reference/scenario-format/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
- [Estimates are not measurements](/concepts/estimates-are-not-measurements/)
