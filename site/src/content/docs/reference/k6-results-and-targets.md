---
title: k6 result format and target rules
description: The rules for load-test targets, what a generated k6 script does, the result formats and the comparison rules.
---

This page is the reference for load-test targets, generated k6 scripts and measured results.

## Targets

A target is a named base URL of a deployed system. A target belongs to one project. A project holds at most 20 targets. A request for target 21 gets `409`.

A target has a `name`, a `baseUrl` and an `environment`. The name is one line of at most 200 characters.

When a target is removed, its scripts and its results stay. They hold the target as it was.

### Base URL

- The URL is an absolute `http` or `https` URL of at most 2000 characters.
- The URL holds no credentials.
- The URL holds no query and no fragment.
- Vellum stores the scheme and the host in lower case and removes a slash at the end of the path.
- The URL can point at a private network. Vellum sends no request to a target.

### Class

A target states one class in `environment`: `development`, `test`, `staging` or `production`. There is no default. A request without a class gets `400`.

### Authorisation

| Rule | Detail |
|---|---|
| Who | A person with the Owner role. An agent token gets 403 `human_only`. |
| What | The caller names the base URL that it read in `baseUrl`. A different base URL is refused with 409. |
| Record | The user, the time, the base URL and the class. |
| Void | A change of the base URL or of the class voids the authorisation. A change of the name does not. |
| Revoke | An owner can revoke the authorisation. An agent token gets 403 `human_only`. |
| Effect | Vellum generates a script for an authorised target only. Vellum accepts a result only while the target of its script is authorised for the same base URL and class. |

### Production defaults and the override

These rules apply to a target of class `production`:

| Subject | Default | With the override |
|---|---|---|
| Non-idempotent operations: `POST`, `PUT`, `PATCH`, `DELETE` | The script leaves the step out and lists it as not covered. | `allowNonIdempotent` puts the step in the script. |
| Failure injection: a scenario that declares faults | Vellum generates no script. | `allowFailureInjection` lets Vellum generate the script. |

The model holds no idempotency fact for an operation. So each of the four methods counts as non-idempotent.

An override has these rules:

- Only a person with the Owner role sets it. An agent token gets 403 `human_only`.
- It applies to one target of class `production`. A request for a target of another class gets `400`.
- The request has `allowNonIdempotent`, `allowFailureInjection`, `reason` and `confirmName`.
- It needs a reason of at most 500 characters.
- It needs the name of the target, typed again in `confirmName`.
- A request with both flags `false` clears the override.
- A change of the base URL or of the class clears it.
- The header of each script states the override, who set it and the reason.

### Audit

Vellum writes these entries to the audit log of the workspace:

| Action | When |
|---|---|
| `load-test.target.authorised` | An owner authorises a target. |
| `load-test.target.authorisation-voided` | An owner revokes the authorisation, or a change voids it. The entry states the cause: `revoked`, `base-url-changed` or `environment-changed`. |
| `load-test.target.override-set` | An owner sets an override. The entry states each of the two permissions as `allowed` or `excluded`. |
| `load-test.target.override-cleared` | An owner clears the override, or a change clears it. The entry states the cause: `cleared`, `base-url-changed` or `environment-changed`. |

An entry names the target, the class and the origin of the base URL. The entry commits with the change.

## The generated script

`POST /api/projects/{projectId}/load-tests/scripts` takes `targetId` and `profileId`. It accepts `?branchId=` for a draft or a variant. Vellum generates the script from the scenario of the stress profile, the model of the branch, the target and the arrivals of the profile. The generator version is `vellum-k6/1`.

The same scenario version, model version, target and profile version give the same text and the same hash. A second request with the same inputs gets the stored script with status 200. A new script gets status 201.

The answer holds the text in `content`, the file name `vellum-k6-<first 12 characters of the hash>.js`, the `plan`, a `command` and a `ciWorkflow`. The plan lists the driven `steps`, the `operations`, the `notCovered` steps and the `notes`. The command is `k6 run` with `VELLUM_BASE_URL`. The workflow is a GitHub Actions workflow that runs the script and keeps the result file. A list of scripts leaves out the text.

Vellum does not run k6 and sends no request to the target.

### Refusals

Vellum generates no script in these cases. The answer has status 422, the code in `type` and the `reasons`.

| Type | Cause |
|---|---|
| `target-not-authorised` | No authorisation is in force for the base URL and the class of the target. |
| `scenario-not-valid` | The scenario is not valid against the model, or the scenario of the profile does not exist. |
| `failure-injection-on-production` | The scenario declares faults, the target is production-class and no override allows failure injection. |
| `no-http-step` | The scenario has no step that the script can drive, or the production rules exclude each such step. |

### Environment variables of the script

| Variable | Use |
|---|---|
| `VELLUM_BASE_URL` | Necessary. The base URL of the authorised target. The script stops in `setup()` when the value is another URL. |
| `VELLUM_AUTHORIZATION` | Optional. The value of the `Authorization` header. |
| `VELLUM_HEADERS` | Optional. A JSON object of more request headers. |
| `VELLUM_RESULT_FILE` | Optional. The path of the result file. The default is `vellum-k6-result.json`. |

The script holds no credential.

### What the script drives

The script plays the caller of the top-level calls of the scenario, in order.

A top-level call is driven when all of these are true:

- The transport of the operation is `http`.
- The name of the operation is a method and a path.
- The playback of the scenario does not skip the step.
- The request fixture has a text, a number or a boolean member for each placeholder of the path.
- The production rules do not exclude the method.

The script resolves a top-level branch step as the playback resolves it.

### What the script does not cover

The header of the script lists each step that the script does not perform, with the reason. No step is left out without an entry.

| Step | Reason |
|---|---|
| A step inside a call | The system under test does it. |
| An `onError` step of a call | The script does not drive the error handling of the caller. |
| A handler step of a `publish` step | A consumer of the system under test does it. |
| A `publish` step or a `write` step | k6 sends HTTP requests only. |
| A `parallel` step | The script sends its requests one after the other. |
| The path of a branch that the playback does not take | The script follows the playback. |
| A call of a `grpc` or `command` operation | k6 sends HTTP requests only. |
| A call of an operation whose name is not a method and a path | The script has no request to send. |
| A call that the playback skips | The script follows the playback. |
| A call whose path has a placeholder with no value in the request fixture | The script cannot build the path. |
| A call that the production rules exclude | The target is production-class. |

### Requests

| Part | Source |
|---|---|
| Method and path | The name of the operation. |
| Path placeholder `{name}` | The member `name` of the request fixture, percent-encoded. |
| Body | The request fixture as canonical JSON, for `POST`, `PUT` and `PATCH`. Other methods send no body. The header of the script has a note for a fixture that is not sent. |
| Headers | `Content-Type: application/json`, the headers of `VELLUM_HEADERS` and the `Authorization` header of `VELLUM_AUTHORIZATION`. |
| Timeout | The timeout of the operation. 60 seconds when the operation declares none. |
| Expected status | A 2xx status when the playback has the step as succeeded. Any other outcome when the playback has it as failed. |
| Tags | `vellum_scenario`, `vellum_operation` and `vellum_step`. |

A value from one response is not used in a later request.

A flow ends at its first request that does not get the expected status.

The script does not inject the faults of the scenario. It expects the outcomes that the faults cause. Inject the faults in the target system before the run.

The script does not apply the outages and the policy overrides of the profile. They are inputs of the simulator.

### Load

| Arrivals of the profile | k6 executor |
|---|---|
| `constant`, `poisson` | `constant-arrival-rate` |
| `ramp` | `ramping-arrival-rate` with one stage |
| `burst` | `ramping-arrival-rate` with a stage for each change of the rate |

k6 starts iterations at an even rate. It does not reproduce the random gaps of a Poisson process.

k6 takes whole iterations for each time unit. The script uses the smallest of a second, a minute and an hour in which each rate of the profile is a whole number. When no unit gives a whole number, the script uses an hour, rounds the rate and states this in a note.

The script starts with one virtual user for each iteration of the highest rate in one second, at most 1000. k6 adds virtual users up to 10 times the highest rate, at most 10 000. When the target answers too slowly for the rate, k6 drops iterations, and the result states how many.

An iteration that starts in the warm-up sends its requests. It adds nothing to the result.

### Safety of model text

Names, step IDs, paths and payloads come from people, from imports and from AI proposals. The script writes each one as a string literal or as part of a line comment, in printable ASCII only. No model text becomes code.

### Hash

The script holds the line `const SCRIPT_HASH = "<64 hexadecimal characters>";`.

The hash is the SHA-256 of the UTF-8 text of the script with those 64 characters replaced by zeros.

## The Vellum result format

`handleSummary` writes the file. The format name is `vellum-k6-result/1`. The default file name is `vellum-k6-result.json`.

```json
{
  "scriptHash": "df2a6095749d7d483b695fd6228df7f37afa687305ab502aaebb34a289866fc6",
  "format": "vellum-k6-result/1",
  "generator": "vellum-k6/1",
  "scenarioId": "e4491c93-5f17-4dbf-aeb2-10cd8953763f",
  "scenarioVersion": 1,
  "modelStreamId": "3006ffb6-6d0e-486f-a4d8-d17d64b4765e",
  "modelVersion": 12,
  "targetId": "a6ce0305-b02c-468e-9a59-4031eab2a3be",
  "environment": "test",
  "baseUrlHash": "14a1dad2562afbc1b52f6e8e7814e830fe250c04d29e2958c57e6dc5e5785c39",
  "startedAt": "2026-10-13T16:21:54.624Z",
  "window": { "warmUpMs": 10000, "elapsedMs": 59988 },
  "flow": {
    "iterations": 2000,
    "failed": 0,
    "droppedIterations": 0,
    "latencyMs": { "count": 2000, "min": 1.574, "mean": 6.718, "p50": 6.72, "p90": 10.076, "p95": 10.519, "p99": 13.989, "max": 36.692 }
  },
  "operations": [
    {
      "operationId": "00000000-0000-0000-0000-000000000010",
      "requests": 2000,
      "failed": 0,
      "latencyMs": { "count": 2000, "min": 1.574, "mean": 6.718, "p50": 6.72, "p90": 10.076, "p95": 10.519, "p99": 13.989, "max": 36.692 }
    }
  ]
}
```

| Member | Meaning |
|---|---|
| `scriptHash` | The hash of the script that made the run: 64 hexadecimal characters in lower case. |
| `format`, `generator` | `vellum-k6-result/1` and `vellum-k6/1`. |
| `scenarioId`, `scenarioVersion` | The scenario and its version when the script was generated. |
| `modelStreamId`, `modelVersion` | The model branch and its version when the script was generated. |
| `targetId`, `environment` | The target and its class. |
| `baseUrlHash` | The SHA-256 of the authorised base URL. |
| `startedAt` | The start of the run. |
| `window.warmUpMs` | The warm-up of the profile. |
| `window.elapsedMs` | The time from the start of the run to the end of its last iteration. |
| `flow.iterations` | Flows that started after the warm-up. |
| `flow.failed` | Flows in which a request did not get the expected status. |
| `flow.droppedIterations` | Iterations that k6 did not start, over the whole run. |
| `flow.latencyMs` | The sum of the request durations of a flow, for flows that did not fail. `null` when there is none. |
| `operations[].requests` | Requests of the flows that started after the warm-up. |
| `operations[].failed` | Requests that did not get the expected status. |
| `operations[].latencyMs` | The durations of the requests that got the expected status. `null` when there is none. |

The percentiles are the ones that k6 computes.

## The k6 summary export

Vellum also reads the JSON that `k6 run --summary-export <file>` writes.

A summary export has no member for an identity. A script that Vellum generated has a metric with the name `vellum_script_<hash>`. That name is in the export, so the export names its script.

Vellum reads the scenario, the versions and the target from the stored script. Vellum reads the numbers from the metrics `vellum_flow_duration`, `vellum_flow_failed`, `vellum_op<n>_duration`, `vellum_op<n>_failed`, `vellum_run_elapsed_ms` and `dropped_iterations`, and the start of the run from `setup_data.startedAtMs`. An export without `setup_data.startedAtMs` is refused. An export that names more than one script is refused.

Vellum refuses a summary export of a script that Vellum did not generate. Such a file cannot state the scenario, the model revision or the target of its numbers.

## Import rules

`POST /api/projects/{projectId}/load-tests/results` takes the file as the body. The body has at most 256 KB. A larger body gets `413`. The JSON nests at most 12 levels deep.

| Check | Refusal |
|---|---|
| The file is JSON of one of the two formats. | 400 |
| The script hash names a script that Vellum generated for this project. | 422 `unknown_script` |
| The generator, the scenario, the versions, the target, the class and the base URL hash of the file are those of the script. A summary export states none of these, so this check does not apply to it. | 422 `identity_mismatch` |
| Each number is a finite number that is 0 or more. | 400 |
| A count is a whole number of at most 1 000 000 000 000. | 400 |
| The failed count is not above the total. | 400 |
| A latency summary counts one duration for each request that got the expected status. | 400 |
| The latency values are in order: minimum, p50, p90, p95, p99, maximum. The mean is between the minimum and the maximum. | 400 |
| A duration is at most one day. | 400 |
| One flow or more started after the warm-up. | 400 |
| The file has each operation of the script once, and no other. | 400 |
| The warm-up is the warm-up of the script. | 400 |
| The run is longer than the warm-up and at most 7 days long. | 400 |
| The run does not start before the script was generated, and not in the future. A difference of 5 minutes between clocks is accepted. | 400 |
| The target of the script is authorised now for the same base URL and class. | 422 `target_not_authorised` |
| The same file is not imported already for the project. | 409 |
| The project holds fewer than 200 results. | 409 |

Vellum does not store a refused file. There is no "unverified" result.

A `400` answer has the type `validation_error` and names the member of the file in `errors`. A body that is empty or is not a JSON object has the type `invalid_body`. A `422` answer has `type`, `title`, `detail` and `errors`.

A stored result has `notCovered`: the steps of the scenario that the script did not perform. The result says nothing about them.

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

A value of a stress profile has the label `measured` only with a `measurementRef` of the form `k6:<result ID>`. The ID is in lower case with dashes.

Vellum refuses a save when the reference names no imported result of the same project.

Vellum refuses the delete of a result that a stress profile names. The answer is `409` and names the profiles.

## Stale results

A result is `stale` when the scenario or the model stream has another version than the one the result is bound to. `staleBecause` lists `model`, `scenario` or both.

A stale result keeps its numbers and its label.

## Estimated versus measured

`GET /api/projects/{projectId}/load-tests/compare/estimate` takes `resultId` and `runId`. It puts a stored stress run beside a measured result of the same scenario. A run and a result of different scenarios get `400`.

The comparison has rows for the flow and for each operation of either side.

Each row has the estimated value with its label, the measured value with its label, and the difference. The difference is the measured value minus the estimated value.

| Metric | Estimated | Measured |
|---|---|---|
| `latencyP50`, `latencyP90`, `latencyP95`, `latencyP99` | The time of a successful attempt in the model. | The duration of a request that got the expected status. |
| `throughput` | Completed work for each second in the model. | Requests that got the expected status, for each second after the warm-up. |
| `errorRate` | For the flow: the share of flows that failed. For an operation: the share of attempts that did not complete. | The share of flows or requests that did not get the expected status. |

A side with no value has the label `unknown` and the value `null`. The difference is then `null`.

`differences` lists what the two sides do not share and what limits the comparison: the model branch, the model version, the scenario version, the profile or its version, a stale result, an outdated run, the steps that the script does not perform and dropped iterations. The comparison gives no verdict.

## Regression between two measured results

`GET /api/projects/{projectId}/load-tests/compare/results` takes `baseline` and `candidate`. It compares two measured results of the same scenario and the same target. Results of different scenarios or of different targets get `400`.

The rows are `latencyP50`, `latencyP95`, `latencyP99`, `throughput` and `errorRate`, for the flow and for each operation. Each row has the baseline, the candidate, `change` and `relative`. The change is the candidate minus the baseline. `relative` is the change divided by the baseline.

| Metric | Regressed | Improved |
|---|---|---|
| Latency | The candidate is more than 10 % and more than 1 ms above the baseline. | The candidate is that much below the baseline. |
| Error rate | The candidate is more than 0.1 percentage points above the baseline. | The candidate is that much below the baseline. |
| Throughput | The candidate is more than 10 % below the baseline. | The candidate is that much above the baseline. |

A row with no value on one side is `unknown`. Every other row is `unchanged`.

A flow or an operation is regressed when one of its rows is regressed. `regressions` is the number of regressed flows and operations.

`differences` lists what the two runs do not share: the script, the scenario version, the model version, the profile and the dropped iterations.

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
| Scripts in a project | 300. The oldest script without a result makes room. When each script has a result, the request gets `409`. |
| Measured results in a project | 200 |
| Size of a result file | 256 KB |
| Depth of the JSON of a result file | 12 levels |
| Length of a run | 7 days |

## Endpoints

| Method and path | Purpose |
|---|---|
| `GET`, `POST /api/projects/{projectId}/load-tests/targets` | List the targets, or add one. |
| `PUT`, `DELETE /api/projects/{projectId}/load-tests/targets/{targetId}` | Change or remove a target. |
| `POST /api/projects/{projectId}/load-tests/targets/{targetId}/authorise` | Authorise the target for the base URL in `baseUrl`. |
| `DELETE /api/projects/{projectId}/load-tests/targets/{targetId}/authorisation` | Revoke the authorisation. |
| `PUT /api/projects/{projectId}/load-tests/targets/{targetId}/override` | Set or clear the production override. |
| `GET`, `POST /api/projects/{projectId}/load-tests/scripts` | List the scripts, or generate one. |
| `GET /api/projects/{projectId}/load-tests/scripts/{scriptId}` | One script with its text. |
| `GET`, `POST /api/projects/{projectId}/load-tests/results` | List the results, or import a result file. |
| `GET`, `DELETE /api/projects/{projectId}/load-tests/results/{resultId}` | Read or delete a result. |
| `GET /api/projects/{projectId}/load-tests/compare/estimate` | A stress run beside a measured result. |
| `GET /api/projects/{projectId}/load-tests/compare/results` | Two measured results. |

When a project is deleted, Vellum deletes its targets, its scripts and its results.

See [Load test with k6](/how-to/load-test-with-k6/) for the procedure and [An estimate is not a measurement](/concepts/estimates-are-not-measurements/) for what a measured result shows.
