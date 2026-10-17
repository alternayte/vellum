---
title: Stale results and Build Ready blockers
description: Why a result, a fact, a saved estimate, a run or a bundle is not current, how to make it current, and what clears each Build Ready blocker.
---

Vellum records the inputs of each result. When an input changes, the result is not current. Vellum does not hide the result and does not count it as a pass.

## A promise result shows (stale)

**Symptom:** In the promise dialog, a result shows **(stale)** and the text **This result is stale. It was made before these changes:**. In the API, the obligation has `current: false` and `changed` lists the inputs that moved.

**Cause:** A run records the model stream and version, the scenario version and hash, each schema version, the repository and the commit. One of these is not the current one any more. A frequent cause is an edit of the model or an accepted import proposal.

**Fix:**

1. Read the list of changes. Decide whether the change is intended.
2. For a scenario check or a source pattern check, run the check again. In the API: `POST /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}/run`.
3. For a test plan, run the test in the repository on the commit that the model follows. Record the evidence again: `POST /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}/evidence`.

A stale pass counts as stale. It does not count as passed.

## A result changed to not-run without a new run

**Symptom:** An obligation that had a result shows `not-run`.

**Cause:** One of these.

- The obligation changed after the run. A run records a hash of the kind and the recipe of the obligation. A new title does not change the hash.
- The run was of the draft revision, and the revision is approved now. A run of a draft does not count after the approval.
- The scenario of a scenario check does not exist, or cannot be played on the model.

**Fix:**

1. Run the check again on the approved revision.
2. If the scenario is gone, link the obligation to a scenario that exists. This needs a new revision of the promise.

## Evidence is counted as not run

**Symptom:** You recorded evidence for a test plan. The result stays `not-run` and the record is a claim.

**Cause:** Vellum could not bind the evidence to a workflow run on GitHub. The record has a reason. A claim is never a result.

| Reason | Fix |
|---|---|
| `no-evidence-source` | Name the repository and the workflow in the test plan. This needs a new revision. |
| `system-not-checkable` | Vellum checks GitHub Actions only. |
| `run-url-not-recognised`, `run-url-host-differs` | Give the URL of a workflow run on the GitHub host of the workspace. |
| `repository-differs`, `run-repository-differs`, `run-repository-unknown` | Give a run of the repository that the evidence source names. |
| `repository-not-followed` | Import the repository and accept a proposal, so that the project follows a ref. |
| `no-tracked-commit`, `commit-differs` | Give a run on the commit that the model follows. |
| `workflow-differs` | Give a run of the workflow that the plan names. |
| `run-not-completed`, `job-not-completed` | Wait for the run, then record the evidence again. |
| `job-not-found`, `jobs-incomplete`, `jobs-unavailable` | Check the job name in the plan. |
| `run-not-success`, `run-not-conclusive`, `job-not-conclusive` | The run was cancelled or skipped. Run it again. |
| `result-differs` | You said one result and GitHub says the other. Record what GitHub says. |
| `run-not-found` | Check the run URL and the access of the token. |
| `github-not-configured`, `github-unavailable`, `github-access-refused`, `github-rate-limited` | See [AI provider and GitHub connection problems](/troubleshooting/providers-and-github/). |

[Promises and evidence](/reference/promises-and-evidence/#identity-reasons) defines each reason.

## An obligation is unsupported

**Symptom:** An obligation has the result `unsupported`.

| Check | Cause | Fix |
|---|---|---|
| Scenario check | Each assertion holds and each is vacuous. The scenario does not exercise what the assertions assert. | Add a fault or an assertion that the claim needs. This needs a new revision. |
| Source pattern check | The scan cannot establish absence: a limit stopped it, or it did not look where the pattern would be. | Scan the ref with all folders, then run the check again. |
| Source pattern check | No accepted import links the named service to a service of the repository. | Accept the import proposal for the service. |
| Other kinds | Vellum cannot check this kind. | Use a test plan with evidence from the repository. |

`unsupported` is not a pass. It is a gap and a blocker.

## An accepted fact is marked stale

**Symptom:** The canvas marks an element or a contract as stale. The build shows a `fact-stale` blocker.

**Cause:** A tracking scan read the files that the fact came from and did not find the fact. Vellum sets the mark on a fact with the label **Observed in source** and proposes its removal.

**Fix:**

1. Open the review of the newest scan.
2. If the fact is gone from the system, accept the **Remove** proposal.
3. If the fact is still true, find why the scan did not see it: a moved file, a changed folder scope or a changed pattern. A later scan that finds the evidence clears the mark.
4. If a person confirms the fact, change it in the model so that it is declared.

A comparison scan marks no fact as not seen. See [Not seen in latest scan](/how-to/import-a-repository/#not-seen-in-latest-scan).

## An assistant answer in the review is stale

**Symptom:** The review shows **The model changed after this answer was made. Ask again before you keep a proposed decision.** The API answers `409` with the type `critique_stale`.

**Fix:**

1. Ask the assistant again.
2. Keep the proposal of the new answer.

## A review snapshot differs from the findings

**Cause:** A snapshot holds the findings of one pinned state with their answers. It does not change when the model changes.

**Fix:**

1. Open the findings. Vellum computes them from the model as it is now.
2. Save a new snapshot.

## A stress run is outdated

**Symptom:** The result shows **Outdated: the … changed after this run.** The run list shows **outdated**.

**Cause:** The model or the scenario changed after the run. The result is true for the old inputs only. The dialog also tells you when the profile has a newer version than the run used.

**Fix:**

1. Run the profile again.
2. Compare the two runs if you want to see the effect. See [Stress a flow](/how-to/stress-a-flow/#compare-two-runs).

## A saved cost estimate is outdated

**Symptom:** A saved estimate shows **outdated** and lists what changed. An estimate with no change shows **inputs unchanged**.

**Cause:** A saved estimate keeps its numbers. A resource, an assumption or a price changed later. An estimate that an earlier engine saved is also outdated.

**Fix:**

1. Read the live estimate. It uses the inputs as they are now.
2. Save a new estimate.

A line with the flag **stale price** is a different matter: the price is older than the limit in the settings. Enter or fetch the price again. See [Estimate costs](/how-to/estimate-costs/).

## A bundle is not current

**Symptom:** `POST /api/projects/{projectId}/build/verify` answers with `current: false`.

The answer has two separate fields.

| Field | Question | When it is `false` |
|---|---|---|
| `matchesPinned` | Is the bundle what its pinned versions generate? | A file or the manifest was changed after the export. Nothing that occurred in the project later can cause this. |
| `current` | Are the pinned versions still the newest? | An input moved after the export. `moved` lists each one with its `kind`: `model`, `project-name`, `scenario`, `schema`, `promise`, `decision`, `run` or `source`. This is drift, not a changed bundle. |

**Fix:**

1. For `matchesPinned: false`, read `files` and `manifestDifferences`. Do not build from the bundle. Export again.
2. For `current: false`, read `moved`. Export again if the agent must build from the newest state.

If a pinned version is no longer stored, the status is `409` and the type is `pinned_input_unavailable`. See [Export for an agent](/how-to/export-for-an-agent/#check-a-bundle).

## The build shows Not build ready

**Symptom:** The **Build** tab shows **Not build ready: <n> blockers** and the list **Blockers**. In the manifest, `buildReady` is `false`.

A blocker prevents Build Ready. It does not prevent an export. The SDD lists each blocker.

| Blocker | Cause | What clears it |
|---|---|---|
| `fact-inferred` | An element or a contract is inferred. | A person confirms it: accept it, or declare it in the model. |
| `fact-unknown` | The truth of an element or a contract is unknown. | Find the fact and declare it, or remove the item. |
| `fact-stale` | An accepted fact is marked stale. | Confirm it or remove it. See the entry above. |
| `scenario-invalid` | A stored scenario cannot be played on the model. | Correct the scenario or the model. See [Trace a scenario](/how-to/trace-a-scenario/). |
| `producer-mismatch` | A scenario publishes a message from an element that is not the producer of the message in the model. | Change the producer of the message or the step of the scenario. |
| `contract-conflict` | Two HTTP operations of one service have the same method and path. | Remove or change one of them. |
| `promise-not-approved` | A promise has no approved revision. | An owner approves it, or you delete the draft. |
| `promise-gap` | The evidence status of the revision lists a gap. One blocker for each gap. | Read the sentence of the gap. It names what is not established. |
| `promise-check-failed` | An obligation failed its newest check. | Correct the model or the code, then run the check again. |
| `promise-evidence-stale` | A result is stale, an obligation has no result or is unsupported, or the revision has no obligation. | Run the checks again, or record evidence for the commit that the model follows. |
| `review-<rule ID>` | A blocking review finding is a concern, and no accepted decision settles it. | Change the model, or an owner accepts a decision that names the finding. The answer `wont-fix` does not clear a blocking finding. |
| `review-<rule ID>`, with "the finding changed since" | A decision named the finding, and the finding changed after that. | Review the finding again, or change the model. |
| `yaml-unrepresentable-number` | A stored schema has a number that a YAML file cannot hold. The contract file is not written. | Change the stored schema. |
| `self-check-failed` | The generated OpenAPI or AsyncAPI documents do not read back as the model. | Read `selfCheck` in the manifest. Report the case if the model is correct. |

A retired promise gives no blocker.

The gaps of a promise are these: the revision is a draft; the content is not the content that was approved; the revision has no obligation; an obligation has no run, has a claim only, is unsupported or has a stale result; an obligation changed after its last run; an assertion that holds is vacuous; a linked item does not exist or changed after the approval.

## See also

- [Promises and evidence](/reference/promises-and-evidence/)
- [Review rules](/reference/review-rules/)
- [Bundle and SDD format](/reference/bundle-and-sdd-format/)
- [Findings are not proof](/concepts/findings-are-not-proof/)
