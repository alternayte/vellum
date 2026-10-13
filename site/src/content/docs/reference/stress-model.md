---
title: Stress model
description: The exact rules of the stress engine: events, arrivals, distributions, statistics and limits.
---

This page states what the stress engine does. The engine version is `2`. A change to any rule on this page is a new engine version.

Version 2 differs from version 1 in five points:

- The bottleneck rule is the saturation rule on this page.
- The completed work and the throughput of an element count successful calls and writes only.
- The end of the run is the last instant with open work.
- The limits are in the result and in the hash.
- Each outage window has a field of its own in the assumptions.

A stored run of version 1 stays readable. It keeps its numbers and its rule, and the result view names its engine version.

## Inputs

A run reads four things:

- The model of the branch, at one version of its stream.
- One saved scenario, at one version.
- One stress profile, at one version.
- The seed of the profile: an unsigned 64-bit integer.

The engine walks the same scenario document as the trace engine. It moves jobs, not payloads. It checks no schema and evaluates no assertion. It does not apply the faults of the scenario.

A scenario that the trace engine refuses is refused.

## Profile document

`POST /api/projects/{projectId}/stress/profiles` and `PUT /api/projects/{projectId}/stress/profiles/{profileId}` take this document as the body. The body has at most 256 KB. Each list is necessary. An empty list is valid.

| Member | Content |
|---|---|
| `name` | One line of at most 200 characters. |
| `scenarioId` | A saved scenario of the project. |
| `seed` | An unsigned 64-bit integer as text in decimal digits, from `0` to `18446744073709551615`. |
| `arrivals` | `kind`, `ratePerSecond`, `toRatePerSecond` for a ramp, `bursts` for a burst, `durationSeconds` and `warmUpSeconds`. |
| `arrivals.bursts[]` | `fromSeconds`, `toSeconds` and `ratePerSecond`. The windows are in time order, do not overlap and are inside the duration. |
| `resources[]` | `elementId`, `concurrency` and `queueCapacity`. One entry for each element. |
| `serviceTimes[]` | `target` and `time`. The target is an operation, a subscription or a store element. `time` has `kind` and the members of its distribution: `ms`, `meanMs`, `medianMs` and `p95Ms`, or `minMs` and `maxMs`. |
| `subscriptions[]` | `subscriptionId`, `consumers` and `queueCapacity`. |
| `branches[]` | `stepId` and `probability`. |
| `overrides[]` | `target` and at least one of `timeoutMs`, `maxAttempts` and `backoffMs`. The target is an operation or a subscription of the flow. A subscription takes no `timeoutMs`. |
| `outages[]` | `target`, `fromSeconds` and `toSeconds`. The target is an element, an operation, a channel or a subscription of the model. |

`arrivals` and each entry of `resources`, `serviceTimes`, `subscriptions`, `branches` and `overrides` has a `label` and a `measurementRef`. The label is `declared` or `measured`. The default is `declared`. A `declared` value has no `measurementRef`. A `measured` value needs a `measurementRef` that names an imported result. See [k6 result format and target rules](/reference/k6-results-and-targets/#measured-values-in-a-stress-profile).

A save checks the ranges and the shape only. A profile that lacks an input of its scenario is saved, and the run names what is missing.

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

The engine does not model cancellation. No deadline passes from a caller to the calls inside it.

### Completed, wasted and failed calls

A call that holds a worker ends in one of three ways. The result counts each way apart.

| Count of the element | The call |
|---|---|
| Completed | Responds with success to a caller that still waits. |
| Wasted | Ends after its caller gave up. |
| Failed inside | Fails because a step inside it fails, while its caller still waits. |

The throughput of an element counts completed work only. The utilisation counts the worker time of all three.

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

A profile can replace the timeout or the retry policy of an operation, and the retry policy of a subscription. The result lists each replaced value next to the value of the model. A value that is equal to the value of the model is not in that list.

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
- **Element latency** is from the arrival of a call or a write at the element to the release of the worker.
- **Wait** is from the arrival at a resource to the start of service.

### Percentiles

Percentiles use the sorted nearest-rank method. The value of percentile p is the value at rank `ceil(p / 100 * n)` of the n kept samples in ascending order.

A summary keeps every sample up to 50 000. Above that number it keeps a uniform reservoir of 50 000 samples (algorithm R) from a generator of its own. The count, the minimum, the mean and the maximum stay exact. The summary states when it used a reservoir.

### Time series

The engine samples at a fixed interval. The interval is the duration divided by 120, rounded up to a whole millisecond. A series has at most 240 points. When the drain is longer than the series, the result states that the series is truncated.

### Time to drain

The time to drain is from the end of the arrivals to the instant no work is left. The run stops 3 600 virtual seconds after the arrivals stop. Work that is left then is reported as not drained.

### End of the run

The end of the run is the last instant at which a flow, a call, a write or a message is open. A timeout timer of a call that has ended is not open work. When work is left at the drain limit, the end of the run is the drain limit.

### Saturation and the bottleneck

A resource is an element with its workers, or a subscription with its consumers.

A resource is **saturated** when one of these is true:

1. It turns work away because its queue is full. This counts rejected calls, rejected writes and overflowed messages, from the end of the warm-up to the end of the run.
2. Its queue reaches the **noise bound** inside the measured window.

The **bottleneck** is the saturated resource with the highest utilisation. The lower ID wins a tie. The result lists every saturated resource.

When no resource is saturated, the result states that the rule names no resource. It does not state that the flow has no bottleneck.

#### The noise bound

A queue with spare capacity also has a backlog at times. The noise bound is a depth that such a queue seldom reaches by chance.

The engine measures four values for each resource inside the measured window:

- n: the work that arrives at the resource.
- u: the utilisation.
- ca²: the squared coefficient of variation of the gaps between arrivals.
- cs²: the squared coefficient of variation of the times that a worker holds work it serves. Work that an outage fails at the worker is not in it.

A coefficient that has no value is 1. The variability is `v = (ca² + cs²) / 2`. It is 1 for Poisson arrivals and exponential service times, and 0 when nothing varies.

The noise bound is the smaller of two depths. It is at least 2. It is at most the `queued` limit plus 1.

**Depth of a steady queue.** The measured utilisation has a standard error of `u * sqrt(2 * v / n)`. The rule uses the upper 1 % point: `w = min(1, u * (1 + 2.326 * sqrt(2 * v / n)))`. With `s = exp(-(1 - w) / v)`, the depth is the smallest d of 2 or more with

`(n * (1 - s) + 1 / (1 - s)) * (1 - s) * s^(d - 1) / (1 - s^d) <= 0.01`

- The first factor bounds the number of times that work starts to wait. A steady queue gives `n * (1 - s)`. A queue that starts empty adds at most `1 / (1 - s)`.
- The second factor is the chance that the queue then reaches depth d before it is empty.
- For v = 1 this is a bound for an M/M/c queue of utilisation w. The exact ratio of that queue is w, and s is not below w.
- For other values of v, s is the decay of the queue length in heavy traffic (Kingman).
- When w is 1, this depth has no value. The other depth is the bound.

**Depth at full load.** `2.576 * sqrt(2 * v * n)`, rounded up. A queue whose arrivals equal its capacity moves like a random walk with variance 2 v for each arrival. Its highest depth in n arrivals passes this value with a chance of 1 %. A queue with spare capacity is below a queue at full load.

Each of the three parts has a chance of at most 1 %: the upper point of the utilisation and the two depths. So the rule names a resource with spare capacity with a chance of at most 3 %.

The measured rate is lower. In 2 000 seeds each of a steady M/M/1 queue with a service time of 10 ms, the rule names the server this often:

| Utilisation | Duration | Runs that name the server |
|---|---|---|
| 0.80 | 60 s | 1 of 2 000 |
| 0.90 | 60 s | 1 of 2 000 |
| 0.95 | 600 s | 1 of 2 000 |

The bound has two limits:

- It assumes service times without a long tail. A lognormal service time with a wide spread can pass the bound by chance more often.
- An overload near full load needs enough arrivals. The rule finds it when the backlog passes the depth at full load, which grows with the square root of n.

#### Backlog windows

A backlog window is a time in which work waits at a resource without a break. It starts when the queue stops being empty. It ends when the queue is empty again. Both instants can be outside the measured window: the start in the warm-up, the end in the drain.

The result lists a backlog window when its highest depth inside the measured window is at or above the noise bound. Each window has its start, its end, its highest depth and the instant of that depth. A window with no end states that work still waits at the end of the run.

A resource lists at most 4 windows: the ones with the highest depth. The result states when the list is full.

#### Queue growth

The statement that a queue grows is separate from the saturation rule.

The queue of a resource **grows** when its depth at the end of the arrivals is above its depth at the middle of the measured window by the noise bound or more. The rate is that difference divided by half of the measured window.

A saturated resource can have a queue that does not grow:

- A full queue with a capacity turns work away and stays at its capacity.
- A backlog from a burst shrinks after the burst.

### Not modelled

Every result lists the effects that the engine leaves out:

- Network latency, jitter and loss between elements.
- Garbage collection pauses, cold starts and the warm-up of a runtime.
- Autoscaling. The number of workers and consumers does not change during a run.
- Database locks, contention and transaction conflicts.
- Connection pools and thread pools other than the declared workers.
- Caches. Every piece of work takes a time from its distribution.
- Payload size.
- Back-pressure from systems downstream of the flow, and load from other flows on the same elements.
- Correlated failures. Only the declared outage windows fail work.
- Clock effects: skew, timer resolution and time-of-day patterns.
- Closed-loop clients. Arrivals do not slow down when the system is slow.
- Capacity shared between the consumers of a subscription and the workers of the same element.
- Retry jitter and growing backoff.
- Consumers of a dead-letter channel.
- Request cancellation and deadline propagation. A caller that gives up does not stop the call it made. The callee and every step behind it go on.

A result adds the faults and the assertions of the scenario when the scenario has them. The engine applies neither.

A result also states the number of inputs of the profile that the flow does not use. Such an input has no effect and is not in the assumptions.

## Confidence

The result states a level and the rule that sets it.

| Level | Rule |
|---|---|
| `low` | A service time on the path of the flow is declared without a measurement. |
| `medium` | Each service time on the path of the flow is measured. |

The engine never reports `high`. The level is not a probability.

## The hash

The hash is SHA-256 over the result in canonical JSON, with the hash member empty. The result holds the engine version, the seed, every assumption and the limits of the run.

Two runs have the same result only when their hashes are equal. The comparison of two runs states this in `sameResult` from the hashes, not from the list of inputs.

## Comparison

`GET /api/projects/{projectId}/stress/compare` takes `left` and `right`, the IDs of two stored runs of the project. Each row of `metrics` has the left value, the right value and `delta`. The delta is right minus left. It is null when one side has no value. The comparison gives no verdict.

The comparison lists the inputs that differ. The first entries are what pins each run: `profile`, `model`, `scenario`, `seed` and `engineVersion`. These keys name one input each:

| Input | Key |
|---|---|
| Arrivals | The field. A burst window has its index. |
| Workers, consumers, service time, model policy | The target and the field. |
| Branch probability | The step ID. |
| Override | The target and the field. |
| Outage window | The target and the place of the window in time order among the windows of that target. |
| Limit | `limits.` and the name of the limit. A run of engine version 1 has `not recorded`. |

## Limits

A run over a limit stops with the issue `stress-too-large` and the name of the limit in `limit`. It stores no result.

| Limit | Value | Name |
|---|---|---|
| Arrival window | 3 600 virtual seconds | `duration` |
| Arrivals. The run is refused before it starts when the mean number of arrivals of the profile is above the limit. | 200 000 | `arrivals` |
| Events | 5 000 000 | `events` |
| Work that waits at one instant | 250 000 | `queued` |
| Events that are scheduled at one instant | 250 000 | `pending` |
| Resources, service times and subscriptions of one flow. The count is the elements with workers, the operations, the stores and two times the subscriptions. | 100 | `targets` |
| Bindings matched for one message | 200 | `routes` |
| Real time | 20 seconds | `wall-clock` |

The real-time limit decides whether a run has a result. It never changes a result. It is not in the result.

The result holds every other limit, with the drain limit, the kept samples and the series points. The limits are in the hash. An instance with other limits gives another hash.

### Memory of one run

The `queued` limit and the `pending` limit bound the memory of one run. A scheduled event is work in service, a backoff or a timeout that has not passed. A timeout of a call that has ended holds no memory of the call.

In the measured worst profiles, one run at these limits holds about 150 MB of managed memory. Without the `pending` limit the same profiles hold more than 800 MB.

### Runs at the same time

| Bound | Value | Answer over the bound |
|---|---|---|
| Runs of the instance at the same time, over all workspaces | 4 | 429 `busy` |
| Runs of one workspace at the same time | 2 | 429 `rate_limited` |
| Runs of one workspace in one minute | 30 | 429 `rate_limited` |

Each 429 answer has a `Retry-After` header. The instance bound of 4 is a default of the server. No setting changes it.

These bounds apply to stored data:

| Bound | Value |
|---|---|
| Profiles of one project | 50 |
| Stored runs of one profile | 20 |

A create of profile 51 gets status 409. A profile keeps its newest 20 runs. Vellum deletes an older run when it stores a new one.

## Profile ranges

| Field | Range |
|---|---|
| Duration | At least 0.001 s, at most 86 400 s |
| Measured window, from the end of the warm-up to the end of the duration | At least 0.001 s |
| Rate | 0 to 100 000 per second. The rate of `constant` and `poisson` is more than 0. A ramp from 0 to 0 is refused. |
| Workers and consumers | 1 to 10 000 |
| Queue capacity | 0 to 1 000 000, or empty |
| Service time | 0 to 3 600 000 ms. The mean of `exponential` and the median of `lognormal` are more than 0. `p95Ms` is at least `medianMs`. `maxMs` is at least `minMs`. |
| Warm-up | 0 or more, and shorter than the duration |
| Outage window | Starts at 0 s or later, ends after it starts and ends at 90 000 s or earlier |
| Probability | 0 to 1 |
| Timeout and backoff override | 0 to 3 600 000 ms |
| Attempts override | 1 to 10 |
| Entries of one list | 200 |
| Outages | 50 |
| Burst windows | 1 to 20 for the pattern `burst` |

## HTTP

| Method and path | Role | Result |
|---|---|---|
| `GET /api/projects/{projectId}/stress/requirements` | Viewer | What a profile for the scenario in `scenarioId` must state |
| `GET /api/projects/{projectId}/stress/profiles` | Viewer | The profiles of the project |
| `POST /api/projects/{projectId}/stress/profiles` | Editor | A new profile |
| `GET /api/projects/{projectId}/stress/profiles/{profileId}` | Viewer | One profile |
| `PUT /api/projects/{projectId}/stress/profiles/{profileId}` | Editor | The profile at its next version |
| `DELETE /api/projects/{projectId}/stress/profiles/{profileId}` | Editor | Deletes the profile and its runs |
| `POST /api/projects/{projectId}/stress/profiles/{profileId}/runs` | Editor | Runs the profile and stores the run |
| `GET /api/projects/{projectId}/stress/profiles/{profileId}/runs` | Viewer | The stored runs, newest first |
| `GET /api/projects/{projectId}/stress/runs/{runId}` | Viewer | One run with its profile and result |
| `DELETE /api/projects/{projectId}/stress/runs/{runId}` | Editor | Deletes one run |
| `GET /api/projects/{projectId}/stress/compare` | Viewer | The differences of the runs in `left` and `right` |

`GET /api/projects/{projectId}/stress/requirements` and `POST /api/projects/{projectId}/stress/profiles/{profileId}/runs` accept `?branchId=` for a draft or a variant.

A stored run states the versions that pin it: the profile, the model stream and the scenario. `outdated` is true when the model or the scenario changed after the run. `outdatedBecause` lists `model`, `scenario` or both.

### Refusals

A refused run gets status 422 with `type`, `title` and `issues`. The type is `stress-too-large` when a limit stopped the run and `stress-refused` in the other cases. Each issue has a `code`, a `target` and a `message`.

| Code | Cause |
|---|---|
| `invalid-profile` | A value of the profile is outside its range, or an override sets a timeout for a subscription. |
| `invalid-scenario` | The trace engine refuses the scenario. |
| `unknown-scenario` | The scenario of the profile does not exist. |
| `missing-concurrency` | An element that does work in the flow has no workers. |
| `missing-service-time` | An operation, a store or a handler of the flow has no service time. |
| `missing-consumers` | A subscription that the flow reaches has no consumers. |
| `missing-branch-probability` | A branch step has no probability. |
| `unknown-target` | An override names no operation and no subscription of the flow, or an outage names nothing of the model. |
| `stress-too-large` | The run is over a limit. |

The engine supplies no default for a missing input.

A run over a bound on runs gets status 429 with `Retry-After`. Its type is `busy` when the instance is full and `rate_limited` when the workspace is.

A save of a profile outside its ranges gets status 400 with the type `validation_error` and the field. A save also gets status 400 when the scenario is not in the project, or when a `measurementRef` names no imported result. The engine counts whole nanoseconds, so the ranges of the duration and of the warm-up apply to the rounded values.

See [Stress a flow](/how-to/stress-a-flow/) for the procedure and [An estimate is not a measurement](/concepts/estimates-are-not-measurements/) for what a result shows.
