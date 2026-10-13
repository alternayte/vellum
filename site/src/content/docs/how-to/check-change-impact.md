---
title: Check the impact of a change
description: Scan another branch of a repository that the project follows, read what the change does to the model and decide whether to follow that branch.
---

This guide shows you how to see what a change in the code does to the model before the model changes. You need the editor role. The project must already follow a ref of the repository: see [Import a repository](/how-to/import-a-repository/).

## Know what is available

Vellum compares one ref of a repository with the model. The comparison is a list of proposals that you can read and cannot accept.

These parts of change impact are not available.

- A scan does not accept a pull request number or a pull request address. Scan the head branch of the pull request.
- A scan does not accept a base commit and a head commit. It reads one ref and compares it with the model, not with another commit.
- Vellum posts no report to a pull request.
- Vellum sets no commit status and blocks no merge in CI.
- The canvas shows no overlay of the change. The proposals are in the review dialog only.

## Scan the other ref

1. Select **Import from GitHub** in the left toolbar.
2. In **Repository**, enter the repository that the project follows, as `owner/name` or a GitHub URL.
3. In **Branch, tag or commit**, enter the branch of the change, for example `remove-outbox`.
4. In **Only these folders**, enter the same folders as the scan that the project follows. Leave it empty for all folders.
5. Select **Scan repository**.

The scan list shows the ref, the commit and the word **comparison**.

The request is `POST /api/projects/{projectId}/imports`. The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make these calls: these endpoints are not open to agent tokens and answer `403`.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/imports" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"repository":"acme/orders","ref":"remove-outbox","includePaths":null,"excludePaths":null}'
```

| Field | Accepted value |
|---|---|
| `repository` | `owner/name`, or the address of the repository page on the GitHub host of the workspace. |
| `ref` | A branch, a tag or a commit SHA. At most 255 characters. No white space. With no `ref`, the scan reads the default ref. |
| `includePaths` | Path prefixes to scan. With none, the scan reads the full repository. |
| `excludePaths` | Path prefixes to leave out. |

## Know when a scan is a comparison

`GET /api/projects/{projectId}/imports/{importId}` gives `kind`, `trackedRef`, `canDecide` and `canDecideReason`.

| Scan | `kind` | Can you accept from it? |
|---|---|---|
| The ref that the accepted facts follow | `tracking` | Yes. |
| A ref, when the repository has no tracked ref yet | `tracking` | Yes. |
| Another branch or tag | `comparison` | No. |
| A commit SHA | `comparison` | No, and it cannot become the tracked ref. |

A comparison scan writes nothing to the model. It marks no fact stale.

## Read the comparison

1. On the completed scan, select **Review <n> proposals** or **Open review**.
2. Read the note at the top. It names the ref that the model follows.
3. Read the proposals.

| Action | Meaning for the change |
|---|---|
| **Add** | The other ref has a thing that the model does not have. |
| **Change** | The other ref differs from a fact that an import wrote. The table shows **Field**, **In the model** and **In the source**. |
| **Conflict** | The other ref differs from a value that a person set. |
| **Remove** | The scan looked at the source of an accepted fact and did not find it. |
| **Match** | Vellum cannot tell which found fact is the one in the model. |

4. Select a proposal. Read the source lines that support it.
5. Read **Accepting this breaks:**. This list shows what the model loses if the proposal is accepted.

The list has these items.

- For a **Remove**: the stored scenarios that name the item, with the steps, the assertions and the faults that name it. Also the items of the model that point at it, such as relationships.
- For the **Remove** of a binding: the publish steps whose message no longer reaches a subscription.
- For a **Change** of the schema of a message or of the route of an operation: the stored scenarios that use it.

Vellum computes the list from the model and the scenarios as they are now.

If the scan shows **The source matches the model. This scan proposes no change.**, the other ref changes nothing that the extraction rules read.

## Example: a branch that removes the outbox

The model follows `main` of `acme/orders`. On `main`, `orders-api` registers a transactional outbox. Vellum holds the accepted fact **Transactional outbox** from the rule `dotnet.masstransit-outbox`.

The branch `remove-outbox` deletes the outbox registration and publishes `OrderCreated` directly to the broker.

1. Scan `remove-outbox`. The scan is a comparison.
2. Open the review. It has a **Remove** proposal for **Transactional outbox**.
3. Select the proposal.
4. Read **Accepting this breaks:** to see the scenarios and the model items that name the outbox.
5. Open **Promises**. Find the promises that have a source pattern check for the outbox. That check can fail when the model follows the branch.

The comparison shows that the registration is gone from the source. It does not show that an order is lost when the broker is down. Only a test in the repository can show that.

## Check what the scan did not look at

A missing fact counts only when the scan read the files that the fact came from.

- A partial scan shows **This scan is partial. A limit stopped it before every file was read, so it proposes no removals and missing items prove nothing.**
- A scan with fewer folders says nothing about the facts in the other folders.
- The review states how many accepted facts the scan did not judge, and why.

In the API, `coverage.linkedFacts` gives the counts.

| Field | The scan did not judge the fact because |
|---|---|
| `scope` | The scope, an exclude pattern or the path filter left out a file of its evidence. |
| `limit` | A limit of the scan left out a file of its evidence. |
| `withheld` | A file of its evidence holds a secret or is not text. |
| `failed` | A file of its evidence could not be fetched, verified or parsed. |

A fact that the scan did not judge gets no removal proposal.

## Follow the other ref

Do this when the change is merged, or when the model must follow the branch.

1. In the scan list, select **Follow this ref** on the comparison scan.
2. Open the review again. You can now accept and reject.

The request is `POST /api/projects/{projectId}/imports/{importId}/track`.

Vellum makes the ref the tracked ref of the repository in this project. It plans the scan again as a tracking scan. The scan records who did this and when.

| Status | Type | Cause |
|---|---|---|
| `409` | `import_not_completed` | The scan is not completed. |
| `409` | `ref_not_trackable` | The scan read a commit SHA and no ref. Scan a branch or a tag. |
| `409` | `already_tracked` | The accepted facts already follow this ref. |
| `409` | `newer_scan_exists` | A newer scan of the same ref is completed. Follow that scan. |
| `409` | `model_busy` or `scan_changed` | Another write ran at the same moment. Nothing was written. Try again. |

When the change merges into the tracked branch, you do not need **Follow this ref**. Scan the tracked branch again.

## What goes stale after you accept

- A tracking scan that no longer finds the source of a fact with the label **Observed in source** marks the fact stale and proposes its removal. The fact stays in the model until you accept the removal.
- Each accepted proposal changes the model. A promise result records the model version that it read. After the model changes, the result is stale. A stale pass is not a pass.
- A review snapshot, a stress run and a bundle also record the model that they read.

Run the checks again after you accept. See [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/).

## What Vellum never claims

- A proposal states what the source shows at one commit. It does not show that the code works.
- A difference in the source is not proof of a change in behaviour.
- The items in **Accepting this breaks:** are the scenarios and model items that name the changed item. Vellum does not claim that other parts of the system are safe.
- Vellum does not run the code of either ref.
