---
title: Promises and evidence
description: The states of a promise, the kinds of obligation, the results, and the rules that bind evidence.
---

This page lists the states, the kinds and the rules of promises and their evidence. To learn what the terms mean, see [Promises and evidence](/concepts/promises-and-evidence/). To write a promise, see [Declare a promise](/how-to/declare-a-promise/).

## Requirement IDs

A requirement ID is the prefix of the project, a hyphen and a number: `ORD-014`. A number has at least three digits. An ID never changes and is never used again, also after its promise is deleted.

The prefix is 2 to 6 upper-case letters. An owner sets it one time with `PUT /api/projects/{projectId}/promises/prefix`. The request has `prefix` and can have `startAt`, the first number, from 1 to 100,000. A second request gives 409: the prefix cannot change. A project with no prefix refuses a new promise with 409.

An obligation ID is the requirement ID, `/O` and a number: `ORD-014/O2`. The key of the obligation is the last part: `O2`.

## States

| State of a promise | Meaning |
|---|---|
| `draft` | No revision was approved. The promise is not a requirement. |
| `approved` | The newest approved revision is in force. |
| `retired` | An approved revision retired the promise. |

A promise has revisions. A revision is `draft` or `approved`. Only a draft changes. A promise has at most one draft revision at a time.

An approval names the revision and its `contentHash`, so it binds the content that the approver read. The hash covers the requirement ID, the revision number, each field and link of the content, and each obligation with its recipe. When the draft is not the one that the approver read, the approval gives 409 `revision_changed` with the `revision` and the `contentHash` of the draft as it is now.

A promise that was approved is not deleted. To end it, start a revision with `retire: true` and approve it. A retired promise gets no new revision.

| Origin | Who wrote the first revision |
|---|---|
| `human` | A person. This is the default. |
| `ai-suggested` | A person who saved a suggestion of the assistant. The request of the person says so. |
| `agent-suggested` | An agent token. Vellum sets this origin whatever the request says. |

Whatever the origin, only an owner who is signed in approves. An agent token cannot approve.

## Fields of a revision

| Field | Rule |
|---|---|
| `title` | Required. One line. |
| `claim` | Required. |
| `scope` | Optional. |
| `preconditions`, `expectedOutcome`, `forbiddenOutcome`, `evidenceCriteria` | Optional in a draft. Each is required before approval. |
| `forbiddenEvents` | Each has a `kind` of a trace event and can have a `target`: an element or a contract of the project. |
| `requiresPattern` | `repository` as `owner/name` in lower case, `pattern` (`transactional-outbox` or `in-memory-outbox`) and an optional `service`. |
| `links` | `scenarioIds`, `operationIds`, `channelIds`, `subscriptionIds`, `messageIds`, `elementIds`. Each ID names a thing of the project. |

A revision that retires the promise holds no content and no obligations of its own. Its approval needs none of the fields.

## Endpoints

| Endpoint | What it does | Who |
|---|---|---|
| `GET /api/projects/{projectId}/promises` | Lists the promises with their counts. | A viewer. An agent token with the scope `read`. |
| `POST /api/projects/{projectId}/promises` | Creates a promise with a draft revision. | An editor. An agent token with the scope `propose`. |
| `GET /api/projects/{projectId}/promises/prefix` | Gives the prefix and the next number. | A viewer. An agent token with the scope `read`. |
| `PUT /api/projects/{projectId}/promises/prefix` | Sets the prefix. | An owner, signed in. |
| `GET /api/projects/{projectId}/promises/{promiseId}` | Gives the promise, its revisions and the evidence status of each. | A viewer. An agent token with the scope `read`. |
| `PUT /api/projects/{projectId}/promises/{promiseId}` | Changes the draft revision. | An editor. An agent token with the scope `propose`. |
| `DELETE /api/projects/{projectId}/promises/{promiseId}` | Deletes a promise that was never approved. | An editor. An agent token with the scope `propose`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/revise` | Starts a new draft revision. | An editor. An agent token with the scope `propose`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/approve` | Approves the draft revision. The request has `revision` and `contentHash`. | An owner, signed in. |
| `GET /api/projects/{projectId}/promises/{promiseId}/runs` | Lists the runs. | A viewer. An agent token with the scope `read`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/obligations` | Adds an obligation to the draft. | An editor. An agent token with the scope `propose`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/obligations/generate` | Adds the obligations that Vellum derives from the draft. A second call adds nothing twice. | An editor. An agent token with the scope `propose`. |
| `PUT /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}` | Changes an obligation of the draft. | An editor. An agent token with the scope `propose`. |
| `DELETE /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}` | Removes an obligation from the draft. | An editor. An agent token with the scope `propose`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}/run` | Runs a `scenario-check` or a `source-pattern-check` and stores the run. | An editor. An agent token with the scope `propose`. |
| `POST /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}/evidence` | Records the result of a test outside Vellum for a test plan. | An editor. An agent token with the scope `propose`. |

An obligation changes only inside a draft revision. A write to an approved revision gives 409.

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

An obligation of any other kind is `unsupported`. It never passes. A kind is lower-case letters, digits and hyphens, at most 50 characters. The recipe of such a kind is text only.

The API gives the way that an obligation gets a result in `execution`: `deterministic`, `external-evidence` or `none`.

The recipe of an obligation has `text` and one typed part that fits the kind.

| Kind | Typed part | Content |
|---|---|---|
| `scenario-check` | `scenarioCheck` | `scenarioId`, `faults` and `expect`. `expect` holds at least one assertion. Each target has at most one fault. A fault cannot name a target that the scenario already faults. |
| `source-pattern-check` | `sourcePattern` | `repository`, `pattern`, an optional `service` and `requireUnconditional`. `requireUnconditional` is true by default: a pattern that exists only under a compile-time condition fails the check. |
| `contract-test`, `integration-test`, `failure-test` | `plan` | `steps` and `expectedObservations` (each at least one item), `inputs`, `forbiddenObservations`, `contractIds` and `evidenceSource`. |

An evidence source has `system` (`github-actions`), `repository` (`owner/name` in lower case), `workflow` and an optional `job`. The workflow is the file name of the workflow, its path, or its name. A value that ends in `.yml` or `.yaml` is compared with the file path that GitHub gives. Each other value is compared with the name of the workflow.

A run request can name the input of the check: `modelRevisionId` or `branchId` for a `scenario-check`, `scanId` for a `source-pattern-check`, and `revision` for the revision of the promise. With no `revision`, the run is of the approved revision in force.

## Results

| Result | Meaning |
|---|---|
| `passed` | The check ran and held, for the inputs the run names. |
| `failed` | The check ran and did not hold. |
| `not-run` | No run applies. A generated obligation, a test nobody ran, and a claim Vellum could not check are all `not-run`. |
| `unsupported` | Vellum cannot check this kind, or cannot check it on the input it was given. |

There is no fifth result. A passing bounded test is not proof for other inputs, commits or environments.

| Check | `passed` | `failed` | `not-run` | `unsupported` |
|---|---|---|---|---|
| `scenario-check` | Each assertion holds, and at least one is not vacuous. | An assertion does not hold. | The scenario does not exist, cannot be played on the model, or now faults a target that the obligation faults. | Each assertion holds and each is vacuous: the scenario does not exercise what they assert. |
| `source-pattern-check` | The scan found the pattern, with the condition that the recipe requires. | A tracking scan looked where the pattern would be and did not find it. | The request names no scan, or the scan has no snapshot. | The scan cannot establish absence: it is a comparison scan, or it did not look where the pattern would be. Or no accepted import links the named service to a service of the repository. |
| Test plan | GitHub gives a pass for the bound run. | GitHub gives `failure` or `timed_out` for the bound run. | No evidence is recorded, or the evidence is a claim. | Never. |

A vacuous assertion of a check that passed is a gap of the evidence status.

A run of a draft revision is labelled `draft`. It does not count after the revision is approved. A promise with no approved revision runs a check only when the request names the draft revision.

## Staleness

A run records every input it read: the model stream and version, the scenario version and hash, each schema version, the repository and the commit.

A result is `current` while every input is the one that is current now. When an input moves, `current` is false and the result is stale. A stale pass counts as stale, not as passed. The field `changed` names each input that moved.

A run also records a hash of the kind and the recipe of the obligation. When the obligation changes after the run, the run does not apply and the obligation is `not-run`. A new title does not change the hash.

The evidence status of a revision has the counts (`passed`, `failed`, `notRun`, `unsupported`, `stale`) and a list of gaps. Each gap is one sentence that names what is not established. Vellum computes the status on every read.

The status has a gap for each of these:

- The revision is a draft.
- The content of the revision is not the content that was approved.
- The revision has no obligation.
- An obligation has no run, has a claim only, is unsupported, or has a stale result.
- An obligation changed after its last run, or its last run was of the draft.
- An assertion that holds is vacuous.
- A linked thing does not exist, or it changed after the approval.

## Build blockers

| Blocker | When |
|---|---|
| `promise-not-approved` | The promise has no approved revision. |
| `promise-gap` | The evidence status of the revision in force lists a gap. One blocker for each gap. |
| `promise-check-failed` | An obligation failed its newest check. |
| `promise-evidence-stale` | An obligation has a stale result, has the result `not-run` or `unsupported`, or the revision has no obligation. |

A retired promise gives no blocker. A blocker prevents Build Ready. It does not prevent an export. See [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/) and [Bundle and SDD format](/reference/bundle-and-sdd-format/).

## Evidence binding rules

The evidence request has these fields.

| Field | Rule |
|---|---|
| `result` | `passed` or `failed`. |
| `source.system` | `github-actions` or `other`. |
| `source.runUrl` | An `https` address with no user information. |
| `source.commitSha` | Optional. 40 or 64 lower-case hex digits. |
| `source.conclusion` | Optional. One lower-case word. `success` goes with `passed`. Each other word goes with `failed`. |
| `artifactDigest` | Optional. `sha256:` and 64 lower-case hex digits. |
| `revision` | Optional. The revision of the promise. The default is the approved revision in force. |

A result of a test outside Vellum counts only when Vellum reads from GitHub that all of these are true.

1. The obligation is a test plan that names an evidence source.
2. The run URL is on the GitHub host of the workspace.
3. The run is of the repository that the evidence source names, and the project follows that repository.
4. The run is of the workflow that the evidence source names.
5. The run ran on the commit: the commit you give, or the commit the model follows.
6. The run is completed. If the evidence source names a job, that job ran and completed. A matrix gives more than one job of one name: each of them counts.
7. The conclusion is a pass or a failure. `success` is a pass. `failure` and `timed_out` are a failure. Each other conclusion is neither.
8. The result you give is the result GitHub gives.

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

To connect GitHub, see [Connect GitHub](/how-to/connect-github/). For a claim that does not become a result, see [Providers and GitHub](/troubleshooting/providers-and-github/).

## Refusals

| Status and code | When |
|---|---|
| 400 `validation_error` | A field breaks a rule. `errors` names each field. |
| 400 `invalid_body` | The body is not the expected JSON document. |
| 403 | The caller does not have the role. An agent token calls an endpoint for a signed-in person. |
| 404 `not_found` | The promise, the revision or the obligation does not exist in the project. |
| 409 `conflict` | The write does not fit the state. Examples: the newest revision is approved, a draft exists already, the promise is retired, the promise was approved and cannot be deleted, a limit is reached. |
| 409 `revision_changed` | An approval names a draft that is not the draft as it is now. |
| 409 `no_approved_revision` | A run or evidence names no revision, and the promise has no approved revision. |
| 409 `not_runnable` | A run of a test plan or of an unsupported kind. |
| 409 `not_a_test_plan` | Evidence for an obligation that is not a test plan. |
| 413 `promise_too_large` | The content and the obligations of the revision are larger than the limit. |
| 429 `rate_limited` | The caller recorded too many runs and evidence in a minute. The header `Retry-After` gives the seconds. |

## Limits

| Limit | Value |
|---|---|
| Promises in a project | 2,000 |
| Revisions of a promise | 100 |
| Obligations of a revision | 50 |
| Links of each kind | 50 |
| Forbidden events of a revision | 20 |
| Faults of a scenario check | 50 |
| Assertions of a scenario check | 100 |
| Items in each list of a test plan | 50 |
| Contracts of a test plan | 50 |
| Size of one recipe as JSON | 32,768 characters |
| Name of a workflow | 255 characters |
| Size of a revision as JSON | 262,144 characters |
| Runs of one obligation of one revision | 1,000 |
| Runs and evidence by one caller | 120 in a minute |

A promise at the size limit can still be revised, retired and deleted.
