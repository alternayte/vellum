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
2. Read the base URL.
3. Select **Authorise this base URL**.

The authorisation states that k6 load may go to that base URL. Vellum records who gave it and when.

A change of the base URL or of the class voids the authorisation. Authorise the target again after such a change.

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

The same scenario version, model version, target and profile version give the same script and the same hash.

## Run the script on your machine

1. Select **Download** in the script dialog.
2. Run the command that the dialog shows. This is an example:

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

An error names the member of the file that causes it. Select the member to go to its line in the text.

You can also import the JSON that `k6 run --summary-export` writes. See [k6 result format and target rules](/reference/k6-results-and-targets/).

## Compare the estimate with the measurement

1. Select the measured result.
2. Select a simulation run under **Estimated versus measured**.

The table shows each number from the simulation beside the number from the k6 run. The column of the simulation has the label `Estimated`. The column of the k6 run has the label `Measured`.

A step that the script did not drive has no measured value. The table shows `Unknown` for it. `Unknown` is not zero and it is not a match.

The comparison gives no verdict.

## Find a regression between two runs

1. Run the script again after a change of the system.
2. Import the second result file.
3. Select the second result.
4. Select the first result under **Compare with a baseline run**.

The table gives a verdict for each row: `Regressed`, `Improved`, `Unchanged` or `Unknown`. The rule for the verdict is below the table.

The two results must be of the same scenario and the same target.

## Use a measurement in a stress profile

1. Open the stress profile under **Simulate**.
2. Find the service time.
3. Select the k6 run under **Source of this value**.
4. Select **Save**.

The service time now has the label `measured` and names the result. Vellum refuses the label `measured` for a value that names no imported result of the project.

## When a result is stale

A result is bound to one scenario version and one model version. Vellum marks the result `stale` when the scenario or the model changes.

A stale result keeps its numbers. Generate a new script and run it to measure the flow as it is declared now.

## Test a production system

Do not run a load test against a production system unless the owner of that system agreed to it.

A script for a production-class target leaves out every `POST`, `PUT`, `PATCH` and `DELETE` request. Vellum generates no script for a production-class target when the scenario declares faults.

An owner can set an override on the target. See [k6 result format and target rules](/reference/k6-results-and-targets/).
