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

A value is `measured` only when it names an imported k6 result of the same project, as `k6:<result ID>`. A save with the label `measured` and no such name is refused. A person cannot turn a typed number into a measured one.

A measured input does not make the output measured. The simulator computes every number of a stress result, so every number of a stress result stays `estimated`.

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

## What a measurement is

A measurement is a number that k6 recorded on a deployed system. Vellum does not produce it. Vellum generates the k6 script, you run it, and Vellum imports the result file. See [Load test with k6](/how-to/load-test-with-k6/).

A measured result has the label `measured`. Its provenance names the import: the script, the target, the user and the time.

Vellum accepts a result only when it names a script that Vellum generated for the project. The script binds the numbers to one scenario version, one model version and one authorised target. A file that names no such script is refused. It is not stored with a weaker label.

A measurement has limits too:

- It holds for one run, one load and one target.
- It covers only the steps that the script drove. The result lists the steps that it does not cover.
- It is a statement of the person or the agent who imported the file. Vellum does not run k6 and cannot check that k6 produced the numbers.
- It goes `stale` when the scenario or the model changes.

## Estimates beside measurements

The comparison puts a simulation run beside a measured result. Each side keeps its own label, its own column and its own look. An estimate is amber with the mark "~". A measurement is teal and bold.

A step that the script did not drive has no measured value. The comparison shows `unknown` for it. `unknown` is not zero and it is not a match.

The comparison states differences. It gives no verdict. An estimate that is near one measurement is not confirmed for any other load.
