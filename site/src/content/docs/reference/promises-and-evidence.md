---
title: Promises and evidence
description: The states of a promise, the kinds of obligation, the results, and the rules that bind evidence.
---

This page lists the states, the kinds and the rules of promises and their evidence.

## Requirement IDs

A requirement ID is the prefix of the project, a hyphen and a number: `ORD-014`. A number has at least three digits. An ID never changes and is never used again, also after its promise is deleted.

An obligation ID is the requirement ID, `/O` and a number: `ORD-014/O2`. The key of the obligation is the last part: `O2`.

## States

| State of a promise | Meaning |
|---|---|
| `draft` | No revision was approved. The promise is not a requirement. |
| `approved` | The newest approved revision is in force. |
| `retired` | An approved revision retired the promise. |

A promise has revisions. A revision is `draft` or `approved`. Only a draft changes. An approval names the revision and its content hash, so it binds the text that the approver read.

A promise that was approved is not deleted. To end it, approve a revision that retires it.

| Origin | Who wrote the first revision |
|---|---|
| `human` | A person. |
| `ai-suggested` | The assistant, and a person saved it. |
| `agent-suggested` | An agent token. |

Whatever the origin, only a workspace owner in the app approves.

The origin does not change. A draft revision also has `lastChangedBy` and `lastChangedAt`: who changed its text or its obligations last. The value is `user:<id>` for a person and `agent-token:<id>` for an agent token. A revision that was last changed before Vellum recorded this has no value.

An obligation has a `source`.

| Source | Who wrote the obligation first |
|---|---|
| `generated` | Vellum, from the links of the promise. |
| `manual` | A person. |
| `agent` | An agent token. |

In a draft, an obligation also has `addedBy`, and `lastChangedBy` after a change. An obligation that a new revision copied from an approved one has neither: a person approved it.

An agent token deletes only a draft promise that agent tokens alone wrote. It changes or removes only an obligation that a token added to the draft and that no person changed since. The answer for anything else is 403 with the type `human_only`.

## Kinds of obligation

| Kind | Who runs it | What it shows |
|---|---|---|
| `scenario-check` | Vellum | The declared flow holds, with the faults of the obligation. It is declared behaviour, not a measurement. |
| `source-pattern-check` | Vellum | A scan of a repository found the pattern at one commit. |
| `contract-test` | Your CI | A test plan for a contract. |
| `integration-test` | Your CI | A test plan for a flow through real services. |
| `failure-test` | Your CI | A test plan with a failure injected. |

Vellum runs the first two kinds. They are deterministic: the same inputs give the same result and the same evidence hash.

Vellum cannot run a test plan. A test plan gets a result only from external evidence. A plan must name its evidence source: a repository the project follows, a workflow, and a job if needed.

An obligation of any other kind is `unsupported`. It never passes.

## Results

| Result | Meaning |
|---|---|
| `passed` | The check ran and held, for the inputs the run names. |
| `failed` | The check ran and did not hold. |
| `not-run` | No run applies. A generated obligation, a test nobody ran, and a claim Vellum could not check are all `not-run`. |
| `unsupported` | Vellum cannot check this kind, or cannot check it on the input it was given. |

There is no fifth result. A passing bounded test is not proof for other inputs, commits or environments.

A run of a draft revision is labelled `draft`. It does not count after the revision is approved.

## Staleness

A run records every input it read: the model stream and version, the scenario version and hash, each schema version, the repository and the commit.

A result is `current` while every input is the one that is current now. When an input moves, `current` is false and the result is stale. A stale pass counts as stale, not as passed.

The evidence status of a revision has the counts (`passed`, `failed`, `notRun`, `unsupported`, `stale`) and a list of gaps. Each gap is one sentence that names what is not established. Vellum computes the status on every read.

## Evidence binding rules

A result of a test outside Vellum counts only when Vellum reads from GitHub that all of these are true.

1. The obligation is a test plan that names an evidence source.
2. The run URL is on the GitHub host of the workspace.
3. The run is of the repository that the evidence source names, and the project follows that repository.
4. The run is of the workflow that the evidence source names.
5. The run ran on the commit: the commit you give, or the commit the model follows.
6. The run is completed. If the evidence source names a job, that job ran and completed.
7. The result you give is the result GitHub gives.

When one of these is not true, Vellum stores a claim. The obligation stays `not-run`. The evidence item has `identity: claimed` and an `identityReason`.

`identity: verified` on an evidence item says one thing: Vellum read this run from the system that holds it. It does not say that the promise holds.

Vellum does not run the test. Vellum does not check that the workflow executes the steps of the plan. An artifact digest is recorded as claimed.

## Identity reasons

| Reason | Meaning |
|---|---|
| `no-evidence-source` | The test plan names no evidence source. |
| `system-not-checkable` | The system is not GitHub Actions. |
| `run-url-not-recognised` | The URL is not the URL of a workflow run. |
| `repository-differs` | The URL is of another repository than the evidence source names. |
| `github-not-configured` | The workspace has no GitHub host. |
| `run-url-host-differs` | The URL is on another host than the GitHub of the workspace. |
| `repository-not-followed` | The project has no accepted import and no tracked ref for the repository. |
| `no-tracked-commit` | You gave no commit and the model follows none. |
| `github-unavailable`, `github-access-refused`, `github-rate-limited` | Vellum could not read from GitHub. |
| `run-not-found` | GitHub has no such run. |
| `run-repository-unknown`, `run-repository-differs` | The run belongs to another repository. |
| `workflow-differs` | The run is of another workflow. |
| `commit-differs` | The run ran on another commit. |
| `run-not-completed`, `job-not-completed` | The run or the job is not finished. |
| `job-not-found`, `jobs-incomplete`, `jobs-unavailable` | Vellum could not find the named job, or could not read every job. |
| `run-not-success`, `run-not-conclusive`, `job-not-conclusive` | The conclusion is neither a pass nor a failure of this plan. |
| `result-differs` | You said `passed` and GitHub says failed, or the reverse. |

## Limits

| Limit | Value |
|---|---|
| Promises in a project | 2,000 |
| Revisions of a promise | 100 |
| Obligations of a revision | 50 |
| Links of each kind | 50 |
| Size of a revision as JSON | 262,144 characters |
| Runs of one obligation of one revision | 1,000 |
| Runs and evidence by one caller | 120 in a minute |
