---
title: Load test with k6
description: Generate a k6 script from a scenario, run it against a deployed test system, and import the measured result.
---

This guide shows you how to measure a deployed system with k6 and how to compare the measurement with an estimate.

Vellum writes the k6 script and reads the result file. Vellum does not run k6 and sends no request to your system. You run k6 on your machine or in CI.

## Before you start

You need k6 on the machine that runs the test. See the [k6 installation guide](https://grafana.com/docs/k6/latest/set-up/install-k6/).

You need a scenario that plays without issues. One top-level step of the scenario must call an `http` operation. The name of that operation must be a method and a path, such as `POST /orders`.

When the path has a placeholder, such as `GET /orders/{orderId}`, the request fixture of the step must have a text, number or boolean member with that name. Without it, the script does not drive the step.

You need a stress profile for the scenario. The script takes the arrival rate, the duration and the warm-up from the profile. See [Stress a flow](/how-to/stress-a-flow/).

You need the Editor role to add a target, to generate a script and to import a result. You need the Owner role to authorise a target and to set a production override.

## Add a target

A target is the base URL of a deployed system.

1. Open **Stress** in the left toolbar.
2. Select **Measure**.
3. Select **Add** under **Targets**.
4. Give the target a name in **Name**.
5. Give the base URL in **Base URL**. The URL starts with `http://` or `https://`. Do not put credentials, a query or a fragment in the URL.
6. Select the class in **Class of the system**: `development`, `test`, `staging` or `production`. Vellum selects no class for you.
7. Select **Add target**.

Vellum sends no request to the base URL. The URL can be on a private network.

A project holds 20 targets at most.

## Authorise the target

Only an owner of the workspace does this step. An agent token cannot do it.

1. Select the target in the list under **Targets**.
2. Read the base URL.
3. Select **Authorise this base URL**.

The authorisation states that k6 load may go to that base URL. Vellum records who gave it and when. The list shows **Authorised** and the date, or **Not authorised**.

A change of the base URL or of the class voids the authorisation and clears a production override. Authorise the target again after such a change.

To take the authorisation back, select **Revoke the authorisation**.

Vellum writes each authorisation, each revocation and each override to the audit log of the workspace.

## Generate the script

1. Under **k6 script**, select the stress profile in **Stress profile**.
2. Select the target in **Target**.
3. Select **Generate k6 script**.

The dialog shows the steps that the script drives under **Driven steps** and the steps that it does not cover under **Not covered**. Read both lists before you run the script.

Vellum refuses to generate a script in these cases. The panel then shows **No script was generated.** and the reason.

- No owner authorised the target.
- The scenario is not valid against the model.
- The scenario has no step that k6 can drive.
- The target is production-class and the scenario declares faults.

The same scenario version, model version, target and profile version give the same script and the same hash.

A project holds 300 scripts at most. When the project is at the limit, Vellum deletes the oldest script that has no result.

## What the script does

The script plays the caller of the top-level HTTP calls of the scenario. It sends the requests of one flow one after the other. A flow ends at its first request that does not get the expected status.

The script does not do these things:

- It does not perform nested calls, writes, publishes or parallel steps. The system under test does that work.
- It does not use a value from one response in a later request. Each request uses the request fixture of its step.
- It does not inject the faults of the scenario. Inject them in the target system before the run.
- It does not apply the outages and the overrides of the stress profile.
- It does not reproduce the random gaps of Poisson arrivals. k6 starts iterations at an even rate with the same mean.

When the target answers too slowly for the rate, k6 drops iterations. The result states how many.

Do not edit the script. Vellum accepts a result only for the script that it generated.

## Run the script on your machine

1. In the script dialog, select the button that starts with **Download**. The button names the file.
2. Run the command under **Run it**. This is an example:

```sh
k6 run -e VELLUM_BASE_URL='https://orders.test.example' vellum-k6-df2a6095749d.js
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
        run: k6 run load-tests/vellum-k6-df2a6095749d.js
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

Vellum also refuses a file in these cases:

- The file has more than 256 KB.
- The file states another scenario, version or target than the script was generated for.
- The target of the script is not authorised now. The target was removed, its base URL or class changed, or its authorisation was revoked.
- The run starts before the script was generated, or in the future.
- The same file is imported already.

An error names the member of the file that causes it. Select the member to go to its line in the text.

A project holds 200 measured results at most. To remove a result, select the delete button on its row.

You can also import the JSON that `k6 run --summary-export` writes. See [k6 result format and target rules](/reference/k6-results-and-targets/).

An agent token with the `propose` scope can send a result file. The result records the token. See [k6 result format and target rules](/reference/k6-results-and-targets/).

## Compare the estimate with the measurement

1. Select the measured result.
2. Under **Estimated versus measured**, select a run in **Simulation run**.

The list holds the stored runs of the stress profile that the script was generated from. When the profile has no stored run, run it on the **Simulate** tab first.

The table shows each number from the simulation beside the number from the k6 run. The column of the simulation has the label `Estimated`. The column of the k6 run has the label `Measured`.

An operation that the script did not drive has no measured value. The table shows `Unknown` for it. `Unknown` is not zero and it is not a match.

The notes above the table state what the two sides do not share, for example another model version or dropped iterations.

The comparison gives no verdict. See [Estimates are not measurements](/concepts/estimates-are-not-measurements/).

## Find a regression between two runs

1. Run the script again after a change of the system.
2. Import the second result file.
3. Select the second result.
4. Under **Compare with a baseline run**, select the first result in **Baseline**.

The table gives a verdict for each row: `Regressed`, `Improved`, `Unchanged` or `Unknown`. The rule for the verdict is below the table.

A latency row is `Regressed` when the second run is more than 10 % and more than 1 ms above the baseline. An error rate row is `Regressed` when it is more than 0.1 percentage points above the baseline. A throughput row is `Regressed` when it is more than 10 % below the baseline.

The two results must be of the same scenario and the same target.

## Use a measurement in a stress profile

1. On the **Simulate** tab, select **Edit** on the stress profile.
2. Find the service time under **Service times**.
3. Enter the value that the k6 run measured. Vellum does not copy a number from the result into the profile.
4. Select the k6 run in **Source of this value**. The field shows only when the project has an imported result.
5. Select **Save**.

The service time now has the label `measured` and names the result as `k6:<result ID>`. The result dialog shows this name under **Use in a stress profile**.

Vellum refuses the label `measured` for a value that names no imported result of the project.

Vellum does not delete a result while a stress profile names it. Set **Source of this value** back to **Declared: a person states it** first.

A stress run reports the confidence `medium` when each service time on the path of the flow is `measured`. In all other cases it reports `low`.

## When a result is stale

A result is bound to one scenario version and one model version. Vellum marks the result `stale` when the scenario or the model changes.

A stale result keeps its numbers. Generate a new script and run it to measure the flow as it is declared now.

## Test a production system

Do not run a load test against a production system unless the owner of that system agreed to it.

A script for a production-class target leaves out every `POST`, `PUT`, `PATCH` and `DELETE` request. Vellum generates no script for a production-class target when the scenario declares faults.

An owner can set an override on a production-class target:

1. Select the target in the list under **Targets**.
2. Under **Production override**, select what to allow: **Allow non-idempotent operations: POST, PUT, PATCH and DELETE requests** or **Allow failure injection: scripts for a scenario with faults**.
3. Give the reason in **Reason**.
4. Type the name of the target to confirm.
5. Select **Set the override**.

Vellum writes the override into the header of each script and into the audit log. To remove it, select **Clear the override**.

## See also

- [k6 result format and target rules](/reference/k6-results-and-targets/) for the file formats, the rules and the endpoints.
- [Stress a flow](/how-to/stress-a-flow/) to make the stress profile and the simulation run.
- [Stress model](/reference/stress-model/) for the confidence rule.
- [Estimates are not measurements](/concepts/estimates-are-not-measurements/) for the difference between the two labels.
