---
title: Report impact in pull request CI
description: Let a GitHub Actions job scan a pull request in Vellum, write the compact impact report to the job summary, and fail only when an opt-in gate says so.
---

This guide shows you how to put the architecture impact report of a pull request into its CI run. A job of your workflow asks Vellum to scan the pull request, waits for the scan, reads the report and writes it to the job summary. The job fails only when the gate policy of the project is on and its verdict is `fail`.

Vellum writes nothing to GitHub. It reads the pull request with the GitHub settings of the workspace. Your workflow reads the report and does what its own steps say.

You need the owner role in the workspace. The project must already follow a ref of the repository: see [Import a repository](/how-to/import-a-repository/).

## Know what the gate does

The report holds no score. Each entry has the label `definite`, `possible` or `unknown`, and each promise has a standing. See [Check the impact of a change](/how-to/check-change-impact/).

| Policy | Verdict | When |
|---|---|---|
| `off` | `off` | Always. This is the default. |
| `fail-on-broken` | `fail` | An approved promise is `broken`: a deterministic check of it failed against this scan. |
| `fail-on-broken` | `pass` | All other cases. |

An entry that is `at-risk`, `possible` or `unknown` never fails the gate. A draft promise never fails the gate.

A pull request scan is a comparison scan. A source pattern check cannot fail against a comparison scan, because a comparison cannot show that a pattern is absent. With the checks of this version, the verdict of a pull request scan is therefore `pass` or `off`. A check can fail against a scan of the ref that the model follows.

## Set the gate policy

1. Scan a pull request or a ref of the repository. On the completed scan, select **Impact**.
2. In the **Gate** box, in **Policy**, select **Fail on a broken approved promise**.

The audit log of the workspace records the change with the old and the new policy. Only a person with the owner role can change the policy. An agent token cannot.

## Create a token for CI

1. Open the workspace **Settings**, then **Agent tokens**.
2. Enter a name, for example `orders CI`.
3. Select **Read** and **Scan pull requests**.
4. Select **Create token** and copy the token.

| Scope | What the CI job does with it |
|---|---|
| `scan-pull-request` | Starts a scan of a pull request. A token with this scope cannot scan a branch, a tag or a commit. |
| `read` | Reads the state of the scan and its report. |

The token acts for the person who created it. That person needs the editor role or higher to start a scan. To restrict the token to the project, create it with the API and a `projectId`. See [Connect a coding agent](/how-to/connect-a-coding-agent/).

In the GitHub repository, add these values under **Settings → Secrets and variables → Actions**.

| Name | Kind | Value |
|---|---|---|
| `VELLUM_TOKEN` | Secret | The token. |
| `VELLUM_URL` | Variable | The address of Vellum, for example `https://vellum.example.com`. The runner must reach it. |
| `VELLUM_PROJECT` | Variable | The project ID. |

## Add the script

Save this script in the repository as `.github/vellum-impact.sh`. It needs `curl` and `jq`. The GitHub runners have both.

<!-- snippet: live -->
```sh
#!/usr/bin/env bash
# Scans the pull request in Vellum, writes the impact report to the job summary and
# fails only when the gate verdict is "fail". A scan that does not complete gives no
# verdict: the script says so and does not fail the job.
set -euo pipefail
: "${VELLUM_URL:?}" "${VELLUM_TOKEN:?}" "${VELLUM_PROJECT:?}" "${VELLUM_REPOSITORY:?}" "${PR_NUMBER:?}"
summary="${GITHUB_STEP_SUMMARY:-/dev/stdout}"
api="$VELLUM_URL/api/projects/$VELLUM_PROJECT/imports"
auth="Authorization: Bearer $VELLUM_TOKEN"

no_verdict() {
  printf 'Vellum gives no verdict: %s\n' "$1" >> "$summary"
  echo "::warning::Vellum gives no verdict: $1"
  exit 0
}

body=$(jq -n --arg repository "$VELLUM_REPOSITORY" --argjson pullRequest "$PR_NUMBER" '{repository: $repository, pullRequest: $pullRequest}')
scan=$(curl -fsS -X POST "$api" -H "$auth" -H "Content-Type: application/json" -d "$body" | jq -r .id) \
  || no_verdict "the scan did not start."

status=queued
for _ in $(seq 1 120); do
  status=$(curl -fsS "$api/$scan" -H "$auth" | jq -r .status) || no_verdict "the state of scan $scan is not readable."
  case "$status" in
    completed) break ;;
    failed | cancelled) no_verdict "scan $scan is $status." ;;
  esac
  sleep 5
done
[ "$status" = completed ] || no_verdict "scan $scan did not complete in time."

curl -fsS "$api/$scan/impact/report" -H "$auth" -H "Accept: text/markdown" -o vellum-impact.md \
  || no_verdict "the report of scan $scan is not readable."
verdict=$(curl -fsS "$api/$scan/impact/report" -H "$auth" -H "Accept: application/json" | jq -r .gate.verdict) \
  || no_verdict "the report of scan $scan is not readable."
cat vellum-impact.md >> "$summary"
echo "Vellum gate verdict: $verdict"
[ "$verdict" != fail ]
```

The script reads the report two times: once as Markdown for the summary, and once as JSON for the verdict. The same scan, model, promises, check results and policy give the same report. Only a change of the policy or a new check run between the two reads makes them differ.

A scan that does not start, fails or does not complete gives no verdict. The script writes a warning and does not fail the job. To fail the job in that case, change `exit 0` in `no_verdict` to `exit 1`.

## Add the workflow

Save this workflow as `.github/workflows/architecture-impact.yml`.

```yaml
name: Architecture impact
on:
  pull_request:

permissions:
  contents: read

jobs:
  impact:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Vellum impact report
        env:
          VELLUM_URL: ${{ vars.VELLUM_URL }}
          VELLUM_PROJECT: ${{ vars.VELLUM_PROJECT }}
          VELLUM_TOKEN: ${{ secrets.VELLUM_TOKEN }}
          VELLUM_REPOSITORY: ${{ github.repository }}
          PR_NUMBER: ${{ github.event.pull_request.number }}
        run: bash .github/vellum-impact.sh
```

A run of a pull request from a fork gets no secrets from GitHub. The step then stops at `VELLUM_TOKEN`.

## Comment on the pull request (optional)

Vellum does not comment. To put the report on the pull request, add a step of your own that uses the token of the workflow. The job then needs `pull-requests: write`.

```yaml
permissions:
  contents: read
  pull-requests: write
```

```yaml
      - name: Comment the impact report
        if: always() && hashFiles('vellum-impact.md') != ''
        env:
          GH_TOKEN: ${{ github.token }}
        run: gh pr comment "${{ github.event.pull_request.number }}" --body-file vellum-impact.md
```

Each run adds a comment. The report is at most 60,000 characters, below the limit of a GitHub comment.

## Read the report

The report is `GET /api/projects/{projectId}/imports/{importId}/impact/report`. It is Markdown, or JSON for `Accept: application/json`.

| Part | Content |
|---|---|
| **Gate** | The policy, the verdict and the reasons. |
| Header | The pull request with its number, title, state, head and base commits. The scan, its kind, its commit and when it completed. |
| **Changed in the source** | Each change with its certainty and its source references as `path:lines@commit`. |
| **Affected flows** | Each affected step with its certainty and the changes that reach it. |
| **Promises** | Each affected promise with its standing and the result of each check against this scan: `passed`, `failed`, `not-run` or `unknown`. |
| **Unknowns and limits** | What the scan did not look at, the checks that did not run, and what the report does not show. |

The report has fixed bounds: 40 changes, 20 flows with 15 steps each, 40 promises and 3 source references for each change. A line states how many entries it does not show. The full report is `GET /api/projects/{projectId}/imports/{importId}/impact`.

The report holds no time other than the time when the scan completed. The same inputs give the same bytes.

## Example output

The project follows `main` of `acme/orders`. The approved promise ORD-014 requires the transactional outbox. Pull request #1 removes it. The GitHub stand-in of the [import tutorial](/tutorials/import-and-review/) serves this pull request on port 5299. The report names the repository with the host of the GitHub address of the workspace.

With the policy `off`, the summary starts with these lines. The script exits with 0.

```text
## Architecture impact of pull request \#1 on `127.0.0.1:5299/acme/orders`

**Gate: off** (policy `off`)

- The gate policy of this project is off. The report never fails a build.
```

With the policy `fail-on-broken`, the verdict of the pull request is `pass`. ORD-014 is `at-risk`, and its source pattern check cannot fail against a comparison scan. The script exits with 0.

## Troubleshooting

| Answer | Cause | What to do |
|---|---|---|
| `403` `agent_not_allowed` on the scan | The token has no `scan-pull-request` scope, or the request names a `ref`. | Create the token with the scope. Send `pullRequest` and no `ref`. |
| `403` `agent_not_allowed` on the report | The token has no `read` scope, or it is restricted to another project. | Add the scope, or use a token of this project. |
| `403` on the scan with a token that has the scope | The person who created the token has the viewer role. | Give that person the editor role, or create the token as an editor. |
| `409` `scan_in_progress` | A scan of the repository is already queued or running for the project. | Run the job again when that scan ends. |
| `429` `scan_limit_reached` | The workspace started its scans of the day. | Raise `usage.maxScansPerDay` in the workspace settings. |
| `pull_request_not_found` in the scan | GitHub has no pull request with this number that Vellum can see. | Check the GitHub settings of the workspace. See [Connect GitHub](/how-to/connect-github/). |
