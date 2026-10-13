---
title: Load test with k6
description: Generate a k6 script from a scenario, run it against a deployed test system, and import the measured result.
---

This guide shows you how to measure a deployed system with k6 and how to compare the measurement with an estimate.

Vellum writes the k6 script and reads the result file. Vellum does not run k6 and sends no request to your system. You run k6 on your machine or in CI.

## Before you start

You need k6 on the machine that runs the test. See the [k6 installation guide](https://grafana.com/docs/k6/latest/set-up/install-k6/).

You need a scenario that plays without issues. One top-level step of the scenario must call an `http` operation. The name of that operation must be a method and a path, such as `POST /orders`.

You need a stress profile for the scenario. The script takes the arrival rate, the duration and the warm-up from the profile. See [Stress a flow](/how-to/stress-a-flow/).

You need the Editor role to add a target, to generate a script and to import a result. You need the Owner role to authorise a target.

## Add a target

A target is the base URL of a deployed system.

1. Open **Stress** in the left toolbar.
2. Select **Measure**.
3. Select **Add** under **Targets**.
4. Give the target a name.
5. Give the base URL. Do not put credentials or a query in the URL.
6. Select the class of the system: `development`, `test`, `staging` or `production`. Vellum selects no class for you.
7. Select **Add target**.

## Authorise the target

Only an owner of the workspace does this step. An agent token cannot do it.

1. Open the target.
2. Read the base URL and the class under **You authorise k6 load against**.
3. Select **Authorise as** with the class.

The authorisation states that k6 load may go to that base URL as a system of that class. Vellum records who gave it and when.

The request names the base URL, the class and the version that the dialog shows. When another person changed the target in the meantime, Vellum authorises nothing. The dialog then shows the target as it is now. Read it and authorise again.

A change of the base URL or of the class voids the authorisation. Authorise the target again after such a change. A script that was generated under the old authorisation gets no result under the new one. Generate a new script.

## Generate the script

1. Select the stress profile under **k6 script**.
2. Select the target.
3. Select **Generate k6 script**.

The dialog shows the steps that the script drives and the steps that it does not cover. Read both lists before you run the script.

Vellum refuses to generate a script in these cases:

- No owner authorised the target.
- The scenario is not valid against the model.
- The scenario has no step that k6 can drive.
- The target is production-class and the scenario declares faults.
- A rate of the profile is too low for k6. k6 takes whole iterations for each hour. Vellum does not raise the rate.

The same scenario version, model version, target and profile version give the same script and the same hash.

## Run the script on your machine

1. Select **Download** in the script dialog.
2. Run the command that the dialog shows. This is an example:

```sh
k6 run -e VELLUM_BASE_URL='https://orders.test.example' vellum-k6-ef6f2900a8c9.js
```

The script reads these values from the environment of k6:

| Variable | Required | Value |
|---|---|---|
| `VELLUM_BASE_URL` | yes | The base URL of the authorised target. |
| `VELLUM_AUTHORIZATION` | no | The value of the `Authorization` header, for example `Bearer <token>`. |
| `VELLUM_HEADERS` | no | A JSON object of more request headers, for example an API key. |
| `VELLUM_RESULT_FILE` | no | The path of the result file. The default is `vellum-k6-result.json`. |

The script holds no credential. Give a credential in the environment only.

The script stops before its first request when `VELLUM_BASE_URL` is not the authorised base URL.

The script follows no redirect. A `3xx` response counts as an unexpected status. So no request and no credential goes to a URL that you did not authorise.

Run the script with the load that it holds. Do not give `--vus`, `--duration`, `--iterations` or `--stage`. Such an option replaces the load of the profile. The script then stops before its first request and tells you why.

The run writes `vellum-k6-result.json` into the current directory.

## Run the script in CI

1. Commit the script under `load-tests/` in your repository.
2. Copy the workflow from the script dialog, under **GitHub Actions workflow**.
3. Save the workflow under `.github/workflows/`.
4. Add the repository secret `VELLUM_AUTHORIZATION` when the target needs a credential.
5. Start the workflow.
6. Download the artifact `vellum-k6-result` when the workflow ends.

This is the workflow for the example above:

```yaml
name: Vellum k6 load test
on:
  workflow_dispatch:
jobs:
  k6:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: grafana/setup-k6-action@v1
      - name: Run the script that Vellum generated
        env:
          VELLUM_BASE_URL: "https://orders.test.example"
          VELLUM_AUTHORIZATION: ${{ secrets.VELLUM_AUTHORIZATION }}
        run: k6 run load-tests/vellum-k6-ef6f2900a8c9.js
      - name: Keep the result file
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: vellum-k6-result
          path: vellum-k6-result.json
          if-no-files-found: error
```

## Import the result

1. Select **Import results** under **Measured results**.
2. Drop the result file on the dialog, or select **Choose a file**, or paste the text of the file.
3. Select **Import**.

Vellum stores the file only when it names a script that Vellum generated for this project. Vellum does not store a file that it refuses.

Vellum also refuses a file when the run did not apply the load of the script: the run was too short, or the number of iterations is not the planned number. The error states the reason.

An import shows that the file names a script that Vellum generated. Vellum did not run k6 and cannot check that k6 produced the numbers.

Vellum stores one run once. The result file and the summary export of the same run are the same run. The second import is refused and names the stored result.

An error names the member of the file that causes it. Select the member to go to its line in the text.

You can also import the JSON that `k6 run --summary-export` writes. See [k6 result format and target rules](/reference/k6-results-and-targets/).

## Compare the estimate with the measurement

1. Select the measured result.
2. Select a simulation run under **Estimated versus measured**.

The table shows each number from the simulation beside the number from the k6 run. The column of the simulation has the label `Estimated`. The column of the k6 run has the label `Measured`.

A step that the script did not drive has no measured value. The table shows `Unknown` for it. `Unknown` is not zero and it is not a match.

The row **Unexpected status** is the share of requests whose status was not the one that the script expected. It is not always an error rate. See [k6 result format and target rules](/reference/k6-results-and-targets/).

When the scenario declares faults, the table shows `Unknown` on the measured side. The simulation does not apply those faults, so the two sides are of different flows.

The list above the table states each known difference of the two sides: the versions, the shape of the arrivals, rounded rates, outages and overrides that the run did not apply, and the two measured windows. Read the list before you read the table.

The comparison gives no verdict.

## Find a regression between two runs

1. Run the script again after a change of the system.
2. Import the second result file.
3. Select the second result.
4. Select the first result under **Compare with a baseline run**.

The table gives a verdict for each row: `Regressed`, `Improved`, `Unchanged` or `Unknown`. The rule for the verdict is below the table.

The two results must be of the same scenario and the same target.

## Use a measurement in a stress profile

Only the service time of an operation that the script drove can be measured.

1. Open the stress profile under **Simulate**.
2. Find the service time of the operation.
3. Select the distribution.
4. Select the k6 run under **Source of this value**. The list shows the runs of the scenario of the profile.
5. Select **Save**.

Vellum writes the numbers of the result into the service time. You cannot type them. The service time now has the label `measured` and names the result.

| Distribution | Numbers from the measured request latency of the operation |
|---|---|
| Fixed | The mean. |
| Exponential | The mean. |
| Lognormal | The median (p50) and the 95th percentile. |
| Uniform | The minimum and the maximum. |

The measured latency is the time from the request to the response as k6 saw it. It holds the network, the wait at the target and the steps inside the call. It is not a pure service time.

Vellum refuses the label `measured` in these cases:

- The service time names no imported result of the project.
- The result is of another scenario than the profile.
- The result has no latency for the operation.
- The value is not a service time: workers, consumers, a branch probability, an override or the arrivals.

## When a result is stale

A result is bound to one scenario version and one model version. Vellum marks the result `stale` when the scenario or the model changes.

A stale result keeps its numbers. Generate a new script and run it to measure the flow as it is declared now.

A service time that names a stale result shows `stale measurement` in the profile and in each run. A stale measurement does not raise the confidence of a run.

## Test a production system

Do not run a load test against a production system unless the owner of that system agreed to it.

A script for a production-class target leaves out every `POST`, `PUT`, `PATCH` and `DELETE` request. Vellum generates no script for a production-class target when the scenario declares faults.

An owner can set an override on the target. See [k6 result format and target rules](/reference/k6-results-and-targets/).
