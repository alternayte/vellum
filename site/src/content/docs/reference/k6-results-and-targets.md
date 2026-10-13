---
title: k6 result format and target rules
description: The rules for load-test targets, what a generated k6 script does, the result formats and the comparison rules.
---

This page is the reference for load-test targets, generated k6 scripts and measured results.

## Targets

A target is a named base URL of a deployed system. A target belongs to one project. A project holds at most 20 targets.

### Base URL

- The URL is an absolute `http` or `https` URL.
- The URL holds no credentials.
- The URL holds no query and no fragment.
- Vellum stores the scheme and the host in lower case and removes a slash at the end of the path.
- The URL can point at a private network. Vellum sends no request to a target.

### Class

A target states one class: `development`, `test`, `staging` or `production`. There is no default. A request without a class is refused.

### Authorisation

| Rule | Detail |
|---|---|
| Who | A person with the Owner role. An agent token gets 403 `human_only`. |
| What | The request has `baseUrl`, `environment` and `version`: the stored base URL, the class and the version of the target that the caller read. |
| Conflict | Vellum refuses the request with 409 when the target has another base URL, another class or another version. It stores nothing. |
| Record | The user, the time, the base URL and the class. |
| Void | A change of the base URL or of the class voids the authorisation. The removal of the target voids it too. |
| Revoke | An owner can revoke the authorisation. |
| Effect | Vellum generates a script for an authorised target only. Vellum accepts a result only while the authorisation that its script was generated under is in force. |

An authorisation that an owner gives after a revoke is a new authorisation. It does not cover a script of the earlier one. Generate a new script.

### Production defaults and the override

These rules apply to a target of class `production`:

| Subject | Default | With the override |
|---|---|---|
| Non-idempotent operations: `POST`, `PUT`, `PATCH`, `DELETE` | The script leaves the step out and lists it as not covered. | `allowNonIdempotent` puts the step in the script. |
| Failure injection: a scenario that declares faults | Vellum generates no script. | `allowFailureInjection` lets Vellum generate the script. |

The model holds no idempotency fact for an operation. So each of the four methods counts as non-idempotent.

An override has these rules:

- Only a person with the Owner role sets it. An agent token gets 403 `human_only`.
- It applies to one target.
- It needs a reason of at most 500 characters.
- It needs the name of the target, typed again.
- A change of the base URL or of the class clears it.
- The header of each script states the override, who set it and the reason.

### Audit

Vellum writes these entries to the audit log of the workspace:

| Action | When |
|---|---|
| `load-test.target.authorised` | An owner authorises a target. |
| `load-test.target.authorisation-voided` | An owner revokes the authorisation, or a change or the removal of the target voids it. The entry states the cause. |
| `load-test.target.override-set` | An owner sets an override. |
| `load-test.target.override-cleared` | An owner clears the override, or a change or the removal of the target clears it. The entry states the cause. |

An entry names the target, the class and the origin of the base URL. An entry for a permission that ends names the class and the origin that the permission was for. These are the values before the change.

The causes are `revoked`, `cleared`, `base-url-changed`, `environment-changed` and `target-removed`.

## The generated script

### What the script drives

The script plays the caller of the top-level calls of the scenario, in order.

A top-level call is driven when all of these are true:

- The transport of the operation is `http`.
- The name of the operation is a method and a path.
- The playback of the scenario does not skip the step.
- The request fixture has a value for each placeholder of the path.
- The production rules do not exclude the method.

The script resolves a top-level branch step as the playback resolves it.

### What the script does not cover

The header of the script lists each step that the script does not perform, with the reason. No step is left out without an entry.

| Step | Reason |
|---|---|
| A step inside a call | The system under test does it. |
| A `publish` step or a `write` step | k6 sends HTTP requests only. |
| A `parallel` step | The script sends its requests one after the other. |
| The path of a branch that the playback does not take | The script follows the playback. |
| A call of a `grpc` or `command` operation | k6 sends HTTP requests only. |
| A call that the production rules exclude | The target is production-class. |

### Requests

| Part | Source |
|---|---|
| Method and path | The name of the operation. |
| Path placeholder `{name}` | The member `name` of the request fixture, percent-encoded. |
| Body | The request fixture as JSON, for `POST`, `PUT` and `PATCH`. Other methods send no body. |
| Timeout | The timeout of the operation. 60 seconds when the operation declares none. |
| Expected status | A 2xx status when the playback has the step as succeeded. A status that is not 2xx and not 3xx when the playback has it as failed. |
| Redirects | The script follows none. A 3xx status is always an unexpected status. |
| Tags | `vellum_scenario`, `vellum_operation` and `vellum_step`. |

A value from one response is not used in a later request.

A flow ends at its first request that gets an unexpected status.

The script sends requests to the authorised base URL only. It follows no redirect, so no request and no `Authorization` header goes to another URL.

### Unexpected status

A status is unexpected when it is not the status that the script expects for the step.

- For a step that expects success, each status outside 2xx is unexpected.
- For a step that expects a failure, a 2xx status is unexpected.
- A 3xx status is unexpected for each step.

So the count of unexpected statuses is an error count only when no step expects a failure.

The script keeps two latency summaries for each operation. The first one holds the requests that expected a 2xx status and got one. The second one holds the requests that expected a failure and got one. The script does not mix them.

A flow has a latency only when no step of the script expects a failure.

### Load

| Arrivals of the profile | k6 executor |
|---|---|
| `constant`, `poisson` | `constant-arrival-rate` |
| `ramp` | `ramping-arrival-rate` with one stage |
| `burst` | `ramping-arrival-rate` with a stage for each change of the rate |

k6 starts iterations at an even rate. It does not reproduce the random gaps of a Poisson process.

k6 takes whole iterations for each time unit. The script uses a second, a minute or an hour as the unit, so that each rate is a whole number. A rate that is not a whole number of iterations in an hour is rounded, and the script states that in a note.

Vellum generates no script in these cases, with the refusal `rate-too-low`:

- A rate is more than 0 and rounds to no iteration in an hour.
- The profile starts fewer than one iteration in its duration.

Vellum does not raise a rate. A rate of 0 inside a ramp or a burst window is valid.

### The load is bound to the script

The script holds the executor options in one line, for example `constant-arrival-rate rate=40 unit=1000 duration=60000 pre=40 max=400`.

In `setup()` the script reads the options that k6 runs, from `exec.test.options.scenarios`. It stops the run when they are not the generated options. The k6 options `--vus`, `--duration`, `--iterations` and `--stage` replace the scenarios of a script, so a run with one of them stops before its first request.

The result file states the options that k6 ran, the planned duration, the planned iterations, and the iterations that k6 completed and dropped.

An iteration that starts in the warm-up sends its requests. It adds nothing to the result.

### Safety of model text

Names, step IDs, paths and payloads come from people, from imports and from AI proposals. The script writes each one as a string literal or as part of a line comment, in printable ASCII only. No model text becomes code.

### Hash

The script holds the line `const SCRIPT_HASH = "<64 hexadecimal characters>";`.

The hash is the SHA-256 of the UTF-8 text of the script with those 64 characters replaced by zeros.

Each line of the script ends with a line feed. The hash does not depend on the system that runs Vellum.

### Output of the run

At the end of a run the script prints a summary. It prints printable ASCII only. It replaces each other character with `?`, so a name of the model cannot send a control sequence to the terminal.

When the script stops in `setup()`, it writes no result file and says so.

## The Vellum result format

`handleSummary` writes the file. The format name is `vellum-k6-result/2`. The generator is `vellum-k6/2`.

```json
{
  "scriptHash": "ef6f2900a8c9d4f7fb6ae0d830a1dc81c5b03cacfbe609b83afc598457778fa7",
  "format": "vellum-k6-result/2",
  "generator": "vellum-k6/2",
  "scenarioId": "1183670c-b0c3-4cf4-99cc-7c4c844f5c1d",
  "scenarioVersion": 1,
  "modelStreamId": "6e4c8d61-a51d-4686-9378-1526f9a618ee",
  "modelVersion": 15,
  "targetId": "4bb42d09-46f1-4d13-8107-b2e01808e8a8",
  "environment": "test",
  "baseUrlHash": "056cececee254dbaf7e1b7609d16900e4406c85499d48f39dbe7f68fa5d3a3c9",
  "startedAt": "2026-10-13T17:48:32.526Z",
  "window": { "warmUpMs": 10000, "elapsedMs": 60003 },
  "load": {
    "effective": "constant-arrival-rate rate=40 unit=1000 duration=60000 pre=40 max=400",
    "plannedDurationMs": 60000,
    "plannedIterations": 2400,
    "iterations": 2400,
    "droppedIterations": 0
  },
  "flow": {
    "iterations": 2000,
    "unexpectedStatus": 0,
    "latencyMs": { "count": 2000, "min": 1.416, "mean": 8.407, "p50": 8.041, "p90": 12.46, "p95": 14.429, "p99": 25.739, "max": 40.29 }
  },
  "operations": [
    {
      "operationId": "00000000-0000-0000-0000-000000000010",
      "requests": 2000,
      "unexpectedStatus": 0,
      "expectedFailures": 0,
      "latencyMs": { "count": 2000, "min": 1.416, "mean": 8.407, "p50": 8.041, "p90": 12.46, "p95": 14.429, "p99": 25.739, "max": 40.29 },
      "expectedFailureLatencyMs": null
    }
  ]
}
```

| Member | Meaning |
|---|---|
| `scriptHash` | The hash of the script that made the run. |
| `scenarioId`, `scenarioVersion` | The scenario and its version when the script was generated. |
| `modelStreamId`, `modelVersion` | The model branch and its version when the script was generated. |
| `targetId`, `environment` | The target and its class. |
| `baseUrlHash` | The SHA-256 of the authorised base URL. |
| `startedAt` | The start of the run: the end of `setup()`. |
| `window.warmUpMs` | The warm-up of the profile. |
| `window.elapsedMs` | The time from the end of `setup()` to the start of `teardown()`. |
| `load.effective` | The executor options that k6 ran. |
| `load.plannedDurationMs` | The duration of the profile. |
| `load.plannedIterations` | The iterations that the executor starts in the whole run, warm-up included. |
| `load.iterations` | The iterations that k6 completed in the whole run. |
| `load.droppedIterations` | Iterations that k6 did not start, in the whole run. |
| `flow.iterations` | Flows that started after the warm-up. |
| `flow.unexpectedStatus` | Flows in which a request got an unexpected status. |
| `flow.latencyMs` | The sum of the request durations of a flow, for flows in which each request got a 2xx status. `null` when a step expects a failure. |
| `operations[].requests` | Requests of the flows that started after the warm-up. |
| `operations[].unexpectedStatus` | Requests that got an unexpected status. |
| `operations[].expectedFailures` | Requests that expected a failure and got one. |
| `operations[].latencyMs` | The durations of the requests that expected a 2xx status and got one. `null` when there is none. |
| `operations[].expectedFailureLatencyMs` | The durations of the expected failures. `null` when there is none. |

Vellum does not read a file of the format `vellum-k6-result/1`. A script of generator `vellum-k6/1` did not bind a run to its load. Generate the script again.

The percentiles are the ones that k6 computes.

## The k6 summary export

Vellum also reads the JSON that `k6 run --summary-export <file>` writes.

A summary export has no member for an identity. A script that Vellum generated has a metric with the name `vellum_script_<hash>`. That name is in the export, so the export names its script.

Vellum reads the scenario, the versions and the target from the stored script. Vellum reads the numbers from the metrics `vellum_flow_duration`, `vellum_flow_unexpected`, `vellum_op<n>_duration`, `vellum_op<n>_expected_failure_duration`, `vellum_op<n>_unexpected`, `vellum_run_elapsed_ms`, `iterations` and `dropped_iterations`. Vellum reads the start of the run and the executor options that k6 ran from `setup_data`.

Vellum refuses a summary export of a script that Vellum did not generate. Such a file cannot state the scenario, the model revision or the target of its numbers.

## Import rules

`POST /api/projects/{projectId}/load-tests/results` takes the file as the body. The body has at most 256 KB.

| Check | Refusal |
|---|---|
| The file is JSON of one of the two formats. | 400 |
| The script hash names a script that Vellum generated for this project. | 422 `unknown_script` |
| The script is of the generator `vellum-k6/2`. | 422 `outdated_script` |
| The scenario, the versions, the target, the class and the base URL hash of the file are those of the script. | 422 `identity_mismatch` |
| Each number is a finite number that is 0 or more. | 400 |
| A count is a whole number. | 400 |
| The unexpected statuses and the expected failures are not above the requests. | 400 |
| The success latency counts one duration for each request that expected a 2xx status and got one. | 400 |
| The summary of expected failures counts one duration for each expected failure. | 400 |
| An operation without a step that expects a failure has no expected failure. | 400 |
| The flow has no latency when a step expects a failure. | 400 |
| The latency values are in order from the minimum to the maximum. | 400 |
| The file has each operation of the script once, and no other. | 400 |
| The warm-up is the warm-up of the script. | 400 |
| The run is longer than the warm-up and at most 7 days long. | 400 |
| The executor options that k6 ran are the options of the script. | 400 |
| The run is not shorter than the plan needs. See the tolerance below. | 400 |
| The iterations that k6 completed and dropped are the planned iterations, inside the tolerance. | 400 |
| The run does not start before the script was generated, and it does not start or end in the future. A clock difference of 5 minutes is allowed. | 400 |
| The authorisation that the script was generated under is in force. | 422 `target_not_authorised` |
| The same file is not imported already. | 409. The detail names the stored result. |
| The same run is not imported already in the other format. A run is one script, one start and one length. | 409. The detail names the stored result. |
| The project holds fewer than 200 results. | 409 |

Vellum does not store a refused file. There is no "unverified" result.

### Tolerance of the load check

The tolerance is 5 % of the planned iterations, and at least 2 iterations.

- The sum of the completed and the dropped iterations can differ from the planned iterations by the tolerance.
- The run must last at least the time in which the plan starts its iterations less the tolerance. For a constant rate of 40 for each second and 60 seconds, the plan starts 2400 iterations. The tolerance is 120 iterations. The run must last at least 57 seconds.

k6 does not count an iteration that it stops at the end of the run. A run in which k6 stops more iterations than the tolerance is refused.

Imports of one project take turns. Two imports at the same instant cannot both take the last place, and they cannot store one run twice.

The identity check shows which script a file is for. It does not show that k6 produced the numbers. Vellum does not run k6, so the file is a statement of the caller. The result records who imported it. A result that an agent token imported names the token.

## Labels and provenance

Each number of a measured result has the label `measured`. The provenance of a result holds:

- the kind `k6-import` and the format of the file,
- the script ID and the script hash,
- the SHA-256 of the imported file,
- the target, its base URL and its class when the script was generated,
- the user who imported the file, the agent token when there is one, and the time.

A number that the simulator computes has the label `estimated`. Vellum never gives such a number the label `measured`.

## Measured values in a stress profile

Only the service time of an operation can have the label `measured`. The service time has a `measurementRef` of the form `k6:<result ID>`. The ID is in lower case with dashes.

Vellum refuses a save in these cases:

| Case | Field of the error |
|---|---|
| The reference names no imported result of the same project. | `serviceTimes[n].measurementRef` |
| The result is of another scenario than the profile. | `serviceTimes[n].measurementRef` |
| The result has no latency for the target. The script did not drive the operation, or no request got the expected 2xx status. The target of a store or of a subscription is never covered. | `serviceTimes[n].measurementRef` |
| The label `measured` is on a resource, a subscription, a branch, an override or the arrivals. | `<entry>.label` |

The caller sends the distribution kind and the reference. Vellum takes the numbers from the measured request latency of the operation and stores them. Vellum does not read numbers that the caller sends for a measured service time.

| Kind | Stored numbers |
|---|---|
| `fixed` | `ms` is the mean. |
| `exponential` | `meanMs` is the mean. |
| `lognormal` | `medianMs` is the p50. `p95Ms` is the p95. |
| `uniform` | `minMs` is the minimum. `maxMs` is the maximum. |

The limit of this mapping: k6 measures the time from the request to the response. That time holds the network, the wait for a worker at the target and the steps inside the call. A service time of the model is the time that one call holds a worker without its steps. So a measured service time is an upper estimate, for the load of that run.

A stress profile has the list `measurements`. Each entry has the target, the reference, `measurementStale` and `staleBecause`. `staleBecause` holds `model`, `scenario` or `missing`.

Each assumption of a stress run with the label `measured` has `measurementStale`. It is `true` when the result is bound to another model version or scenario version than the run used. A stale measurement does not count for the confidence level `medium`. See [Stress model](/reference/stress-model/#confidence).

Vellum refuses the delete of a result that a stress profile names. The check and the delete are one unit.

## Stale results

A result is `stale` when the scenario or the model stream has another version than the one the result is bound to. `staleBecause` lists `model`, `scenario` or both.

A stale result keeps its numbers and its label.

## Estimated versus measured

`GET /api/projects/{projectId}/load-tests/compare/estimate?resultId=<id>&runId=<id>` puts a stored stress run beside a measured result of the same scenario.

Each row has the estimated value with its label, the measured value with its label, and the difference. The difference is the measured value minus the estimated value.

| Metric | Estimated | Measured |
|---|---|---|
| `latencyP50`, `latencyP90`, `latencyP95`, `latencyP99` | The time of a successful attempt in the model. | The duration of a request that expected a 2xx status and got one. |
| `throughput` | Completed work for each second in the model. | Requests that got the expected status, for each second after the warm-up. |
| `unexpectedStatusRate` | The share of attempts that did not complete. | The share of requests that got an unexpected status. |

A side with no value has the label `unknown` and the value `null`. The difference is then `null`.

The measured side is also `unknown` when the two sides are not of the same thing. The row then has a `reason`.

| Case | Rows with an unknown measured side |
|---|---|
| The scenario declares faults. The simulation does not apply them. | Each row. |
| A step of the script expects a failure, and the scenario declares no fault. | The rows of the flow and the rows of the operation of that step. |

The comparison lists each known difference of the two sides in `differences`:

- the branch, the model version, the scenario version and the profile version,
- a stale result and an outdated run,
- the steps that the script does not perform,
- the iterations that k6 dropped,
- the load notes of the script: the shape of the arrivals that k6 does not reproduce, rounded rates and the virtual users,
- the outage windows and the policy overrides of the profile, which the simulation applies and the k6 run does not,
- the measured window of the k6 run and the measured window of the simulation.

The comparison gives no verdict.

## Regression between two measured results

`GET /api/projects/{projectId}/load-tests/compare/results?baseline=<id>&candidate=<id>` compares two measured results of the same scenario and the same target.

| Metric | Regressed | Improved |
|---|---|---|
| Latency | The candidate is more than 10 % and more than 1 ms above the baseline. | The candidate is that much below the baseline. |
| Unexpected status rate (`unexpectedStatusRate`) | The candidate is more than 0.1 percentage points above the baseline. | The candidate is that much below the baseline. |
| Throughput | The candidate is more than 10 % below the baseline. | The candidate is that much above the baseline. |

A row with no value on one side is `unknown`. Every other row is `unchanged`.

A flow or an operation is regressed when one of its rows is regressed.

## Roles and agent tokens

| Action | Role | Agent token |
|---|---|---|
| Read targets, scripts, results and comparisons | Viewer | `read` scope. Targets and scripts also with `export`. |
| Add, change or remove a target | Editor | Not allowed. |
| Authorise a target, revoke, set an override | Owner | Never. 403 `human_only`. |
| Generate a script | Editor | `export` scope. |
| Import a result | Editor | `propose` scope. The same checks apply. |
| Delete a result | Editor | Not allowed. |

## Limits

| Limit | Value |
|---|---|
| Targets in a project | 20 |
| Scripts in a project | 300. The oldest script without a result makes room. The count, the removal and the new script are one unit. |
| Measured results in a project | 200 |
| Size of a result file | 256 KB |
