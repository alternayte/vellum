---
title: A trace is declared behaviour
description: What a trace shows, and what it cannot show.
---

This page explains what a trace is evidence of.

A trace replays a scenario over the contracts in the model. It reads no code and calls no service. It shows what the declared design does when each part behaves as declared, and when a part fails as you tell it to.

A trace can show:

- The order of calls, writes and messages for one behaviour.
- What a declared timeout and a declared retry do to that order.
- Where a duplicate write or a duplicate message appears after a retry.
- Where a message is lost because no dead-letter channel is declared.
- A scenario that references a contract that does not exist.

A trace cannot show:

- That the running system has these contracts.
- Real latency, throughput or failure rates.
- That a retry in the code is safe.

The dock states this on every playback: **Declared behaviour, not a measurement.** Evidence from source code and from test runs has its own labels in Vellum, and a trace never changes them.
