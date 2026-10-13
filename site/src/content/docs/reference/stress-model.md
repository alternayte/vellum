---
title: Stress model
description: The exact rules of the stress engine: events, arrivals, distributions, statistics and limits.
---

This page states what the stress engine does. The engine version is `1`. A change to any rule on this page is a new engine version.

## Inputs

A run reads four things:

- The model of the branch, at one version of its stream.
- One saved scenario, at one version.
- One stress profile, at one version.
- The seed of the profile: an unsigned 64-bit integer.

The engine walks the same scenario document as the trace engine. It moves jobs, not payloads. It checks no schema and evaluates no assertion. It does not apply the faults of the scenario.

A scenario that the trace engine refuses is refused.

## Time and events

- Time is virtual. It is a whole number of nanoseconds.
- All events are in one binary heap.
- Events run in the order (time, sequence). The sequence is the order in which events were scheduled.
- The engine reads no clock and no file.

## Random numbers

- The generator is xoshiro256\*\*. Its state is the first four outputs of SplitMix64 over a seed.
- Each purpose has its own generator: the arrivals, each service time, each branch step and each latency summary. Each one is derived from the seed of the profile and the ID of its target.
- A uniform value in [0, 1) is the top 53 bits of an output times 2^-53.
- The logarithm and the exponential are computed from addition, multiplication, division and square root only. These operations give the same bits on every platform.

## Arrivals

| Pattern | Rule |
|---|---|
| `constant` | Arrival k is at k / rate seconds, for k = 0, 1, 2 and so on. |
| `poisson` | Each gap is `-ln(1 - u) / rate` seconds, with u uniform. |
| `ramp` | A Poisson process. The rate moves in a straight line from `ratePerSecond` to `toRatePerSecond` over the duration. |
| `burst` | A Poisson process at `ratePerSecond`. Inside a window the rate of the window applies. |

For `ramp` and `burst`, the next arrival is the instant at which the integral of the rate has grown by `-ln(1 - u)`.

Arrivals stop at the end of the duration.

## Service times

| Distribution | Sample, in milliseconds |
|---|---|
| `fixed` | `ms` |
| `exponential` | `-meanMs * ln(1 - u)` |
| `lognormal` | `exp(ln(medianMs) + sigma * z)`, with `sigma = (ln(p95Ms) - ln(medianMs)) / 1.6448536269514722` |
| `uniform` | `minMs + (maxMs - minMs) * u` |

- z is a standard normal value from the polar form of the Box–Muller transform. Draw u and v uniform in (-1, 1) until s = u² + v² is in (0, 1). Then z = u * sqrt(-2 ln(s) / s).
- The mean of the lognormal distribution is `medianMs * exp(sigma² / 2)`.
- A sample is rounded to a whole nanosecond.
- A sample is at most 86 400 000 ms.

A service time belongs to an operation, to the handler of a subscription or to the writes of a store.

## Calls

1. The caller starts an attempt. If the operation or its element is in an outage window, the attempt fails at once.
2. The caller starts the timeout of the operation. A timeout of 0 means none.
3. The call asks the element for a worker.
   - A free worker takes the call at once.
   - With no free worker, the call waits in the queue of the element. The queue is first in, first out.
   - With a full queue, the call is rejected. The attempt fails at once.
4. The worker holds the call for one service time. Then it holds the call while the steps inside the call run. Then the call responds and the worker is free.
5. If a step inside the call fails, the attempt fails.
6. A response at the instant of the timeout is late.

When an attempt fails and the retry policy has attempts left, the next attempt starts after the backoff. The backoff is fixed. After the last attempt, the `onError` steps run. Without `onError` steps, the step fails and the rest of its sequence does not run.

### Wasted work

A timeout fails the attempt at the caller. It does not stop the element. The call stays in the queue or on its worker, and the element finishes it. The result counts this as wasted work, with the worker time that it took.

### Outages and waiting calls

A call that waits when an outage starts fails when it reaches a worker. A call that a worker already holds goes on.

## Writes

A write asks the store for a worker and holds it for one service time. A write has no timeout and no retry, because the model declares none. A write to a full queue fails. A write to a store in an outage window fails.

## Messages

1. A publish to a channel in an outage window fails the step.
2. The message goes to each subscription on the channel, and along each binding that the routing key matches. The rules are the rules of the trace engine. A channel takes one message once.
3. A bound channel in an outage window closes that route. The subscriptions behind it lose the message. The result counts it as dropped.
4. The publisher does not wait.

Each subscription has consumers and a queue of its own.

1. A free consumer takes the message at once. With no free consumer, the message waits. The queue is first in, first out.
2. With a full queue, the message is dead-lettered or dropped. The result counts it as overflowed.
3. The consumer holds the message for one service time of the handler. Then it holds the message while the handler steps run.
4. A delivery fails when the subscription or its consumer element is in an outage window, or when a handler step fails.
5. A failed delivery waits the backoff of the subscription and joins the back of the queue. This repeats until the attempts of the retry policy are spent.
6. After the last attempt, the message goes to the dead-letter channel. When the subscription declares none, or that channel is in an outage window, the message is dropped.

A dead-lettered message is counted. It is not delivered to the consumers of the dead-letter channel.

The consumers of a subscription and the workers of the same element are separate pools.

## Parallel and branch steps

- The branches of a parallel step start at the same instant. The step ends when the last branch ends. It fails when a branch fails.
- A branch step takes its `then` path when a uniform value is below the probability of the profile. It takes its `else` path otherwise.

## Overrides

A profile can replace the timeout or the retry policy of an operation, and the retry policy of a subscription. The result lists each replaced value next to the value of the model.

## Statistics

The run has three parts: the warm-up, the measured window and the drain. The measured window is from the end of the warm-up to the end of the arrivals. The drain is after the arrivals stop.

| Statistic | Covers |
|---|---|
| Counts and latency samples | The flows that start at or after the warm-up, and all the work that they cause, also during the drain. |
| Throughput | Completions inside the measured window, divided by its length. |
| Utilisation | Busy worker time inside the measured window, divided by the workers times its length. |
| Queue depth | The time-weighted depth inside the measured window, in total and for each quarter. |

- **Flow latency** is from the start of a flow to the end of its last synchronous step.
- **Call latency** is from the start of an attempt to its response, for attempts that succeeded.
- **Subscription latency** is from the publish to the end of the handler, for deliveries that succeeded.
- **Wait** is from the arrival at a resource to the start of service.

### Percentiles

Percentiles use the sorted nearest-rank method. The value of percentile p is the value at rank `ceil(p / 100 * n)` of the n kept samples in ascending order.

A summary keeps every sample up to 50 000. Above that number it keeps a uniform reservoir of 50 000 samples (algorithm R) from a generator of its own. The count, the minimum, the mean and the maximum stay exact. The summary states when it used a reservoir.

### Time series

The engine samples at a fixed interval. The interval is the duration divided by 120, rounded up to a whole millisecond. A series has at most 240 points. When the drain is longer than the series, the result states that the series is truncated.

### Time to drain

The time to drain is from the end of the arrivals to the instant no work is left. The run stops 3 600 virtual seconds after the arrivals stop. Work that is left then is reported as not drained.

### Bottleneck

A queue grows when its mean depth rises from each quarter of the measured window to the next, and the last quarter is at least 1 above the first.

The bottleneck is the resource with the highest utilisation among those whose queue grows. The lower ID wins a tie. When no queue grows, the result states **None found**.

A full queue with a capacity does not grow. So a resource that rejects work is not named as the bottleneck. The rejected count shows it.

## The hash

The hash is SHA-256 over the result in canonical JSON, with the hash member empty. The result holds the engine version, the seed and every assumption.

## Limits

A run over a limit stops with `stress-too-large` and the name of the limit. It stores no result.

| Limit | Value | Name |
|---|---|---|
| Arrival window | 3 600 virtual seconds | `duration` |
| Arrivals | 200 000 | `arrivals` |
| Events | 5 000 000 | `events` |
| Work that waits at one instant | 500 000 | `queued` |
| Resources, service times and subscriptions of one flow | 100 | `targets` |
| Bindings matched for one message | 200 | `routes` |
| Real time | 20 seconds | `wall-clock` |

The real-time limit decides whether a run has a result. It never changes a result.

These bounds apply to stored data and to requests:

| Bound | Value |
|---|---|
| Profiles of one project | 50 |
| Stored runs of one profile | 20 |
| Runs of one workspace at the same time | 2 |
| Runs of one workspace in one minute | 30 |

## Profile ranges

| Field | Range |
|---|---|
| Rate | 0 to 100 000 per second |
| Workers and consumers | 1 to 10 000 |
| Queue capacity | 0 to 1 000 000, or empty |
| Service time | 0 to 3 600 000 ms |
| Probability | 0 to 1 |
| Timeout and backoff override | 0 to 3 600 000 ms |
| Attempts override | 1 to 10 |
| Entries of one list | 200 |
| Outages | 50 |
| Burst windows | 20 |

## HTTP

All paths start with `/api/projects/{projectId}/stress`.

| Method and path | Role | Result |
|---|---|---|
| `GET /requirements?scenarioId=` | Viewer | What a profile for the scenario must state |
| `GET /profiles` | Viewer | The profiles of the project |
| `POST /profiles` | Editor | A new profile |
| `GET /profiles/{id}` | Viewer | One profile |
| `PUT /profiles/{id}` | Editor | The profile at its next version |
| `DELETE /profiles/{id}` | Editor | Deletes the profile and its runs |
| `POST /profiles/{id}/runs` | Editor | Runs the profile and stores the run |
| `GET /profiles/{id}/runs` | Viewer | The stored runs, newest first |
| `GET /runs/{runId}` | Viewer | One run with its profile and result |
| `DELETE /runs/{runId}` | Editor | Deletes one run |
| `GET /compare?left=&right=` | Viewer | The differences of two runs |

`GET /requirements` and `POST /profiles/{id}/runs` accept `?branchId=` for a draft or a variant.

A refused run gets status 422 with the issues. A run over the rate gets status 429 with `Retry-After`.
