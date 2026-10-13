---
title: An estimate is not a measurement
description: What a stress result tells you, and what it cannot tell you.
---

This page explains what a stress result is evidence of.

A stress run simulates one scenario under a load that you declare. It reads no code and calls no service. It moves jobs through queues and workers that exist only in the model.

## What the result is

The result is the output of a queueing model. The inputs are your numbers: the arrival rate, the workers, the service times and the queue capacities. The model applies them to the flow, the timeouts and the retry policies of the design.

Every number in the result has the label `estimated`. Vellum never changes that label to `measured`.

A result can show:

- Which resource of the model saturates first under the declared load.
- How a timeout, a retry policy or a queue capacity changes the estimate.
- How two sets of assumptions differ.
- That a declared design cannot keep up with a declared load.

A result cannot show:

- What a running system does under that load.
- That a system can handle a load. Vellum never says so.
- That your service times are correct.

## Assumptions have labels

Every input is an assumption. The result lists each one with a label.

- `declared`: a person entered the value.
- `measured`: the value names a measurement that Vellum imported.

This version of Vellum imports no measurements. So every value is `declared`, and a save with the label `measured` is refused. A person cannot turn a typed number into a measured one.

## Confidence is a rule, not a probability

The result states a confidence level and the rule that sets it.

- `low`: a service time on the path of the flow is declared without a measurement.
- `medium`: every service time on the path of the flow is measured.

The stress engine never reports `high`. A model that leaves out the effects below does not earn it.

## What the model leaves out

Every result shows this list. It is not hidden and it does not depend on the profile.

- Network latency, jitter and loss.
- Garbage collection pauses and cold starts.
- Autoscaling.
- Database locks and contention.
- Connection pools and thread pools other than the declared workers.
- Caches.
- Payload size.
- Back-pressure from systems downstream of the flow, and load from other flows.
- Correlated failures.
- Clock effects.
- Closed-loop clients: arrivals do not slow down when the system is slow.
- Capacity that the consumers of a subscription share with the workers of the same element.
- Retry jitter and growing backoff.
- Consumers of a dead-letter channel.

A running system has all of these.

## The same inputs give the same result

A run is a pure function of the model version, the scenario version, the profile, the seed and the engine version. The same five give the same bytes and the same hash. So a difference between two runs comes from a difference in their inputs, and the comparison lists those inputs.

A hash that repeats shows that the calculation repeats. It does not show that the calculation matches a real system.

## How to use an estimate

Use an estimate to compare designs and to find the first limit of a design. Then measure the real system. Keep the measurement apart from the estimate.
