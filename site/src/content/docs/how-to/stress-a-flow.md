---
title: Stress a flow
description: Put one scenario under a declared load and read the estimate.
---

This guide shows you how to simulate one scenario under load.

A stress run is a simulation of the declared flow. It does not call or measure your system. Every number in the result is an estimate.

## Before you start

You need a scenario that plays without issues. See [Trace a scenario](/how-to/trace-a-scenario/).

You need the Editor role to save a profile and to run it. A Viewer can read profiles and results.

## Create a profile

1. Open **Stress** in the left toolbar.
2. Select **New**.
3. Give the profile a name.
4. Select the scenario.
5. Give a seed. The same seed gives the same result.

## State the arrivals

1. Select a pattern: **Constant**, **Poisson**, **Ramp** or **Burst windows**.
2. Give the rate in flows per second.
3. Give the duration in seconds.
4. Give the warm-up in seconds. Flows that start during the warm-up are simulated and left out of the statistics.

## State the capacity

The form lists what the scenario uses. It asks for nothing else.

1. For each element under **Workers**, give the number of workers. Give a queue capacity, or leave it empty for no bound.
2. For each entry under **Service times**, select a distribution and give its values in milliseconds.
3. For each subscription under **Consumers**, give the number of consumers. Give a queue capacity, or leave it empty for no bound.
4. For each branch step, give the probability of its **then** path, from 0 to 1.

Vellum sets no default for these values. A run without one of them is refused, and the message names what is missing.

Timeouts and retries come from the model. To try another value, fill a field under **Override**. The result lists each override next to the value of the model.

## Add an outage

1. Select **Add an outage**.
2. Select the target: an element, an operation, a channel or a subscription.
3. Give the start and the end of the window in seconds of virtual time.

## Run the profile

1. Select **Save and run**.
2. Read the result.

The result shows these parts:

- Headline numbers: throughput, latency percentiles, completed and failed flows, and the time to drain.
- The bottleneck, with the numbers that name it, or **None found**.
- A latency table for the flow, each operation and each subscription.
- Utilisation and queue depth for each element and each subscription.
- Charts of arrivals, throughput, queue depth and flows in flight over virtual time.
- **Not modelled**: the effects that the estimate leaves out.
- **Confidence** and the rule that sets it.
- **Assumptions**: every input of the run with its label.

Vellum stores the run. A profile keeps its 20 newest runs.

## Compare two runs

1. Change one input of the profile.
2. Select **Save and run**.
3. In the result, go to **Compare with another run**.
4. Select the earlier run.

The comparison lists the inputs that differ and the difference of each metric. It gives no verdict.

Keep the seed when you compare. Runs with different seeds differ by chance too.

## When a run is outdated

A stored run names the version of the model and of the scenario that it used. When either changes, the list marks the run **outdated**. Run the profile again to get a result for the current design.

## When a run is refused

- **A value is missing.** Open the profile and fill the field that the message names.
- **The scenario has issues.** Correct the scenario first.
- **The run is over a limit.** Lower the rate or the duration. The [stress model](/reference/stress-model/) lists the limits.
- **Too many stress runs.** Wait the stated number of seconds.

A refused run stores nothing.
