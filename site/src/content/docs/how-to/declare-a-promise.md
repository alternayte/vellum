---
title: Declare a promise
description: State what must always hold, bind it to the model, add obligations and approve it.
---

This guide shows you how to declare a promise. A promise is a requirement with a stable ID, such as `ORD-014`. You need the editor role to write a draft. You need the owner role to set the prefix and to approve.

## Set the prefix

Each project has one prefix for its requirement IDs. A workspace owner sets it one time. It cannot change, because issued IDs contain it.

1. Open **Promises** in the left toolbar.
2. In **Prefix (2 to 6 letters)**, enter 2 to 6 letters, for example `ORD`. The app sends them in upper case.
3. Select **Set the prefix**.

Over HTTP, `PUT /api/projects/{projectId}/promises/prefix` takes `prefix` and, optionally, `startAt`. The `prefix` is 2 to 6 upper-case letters. The `startAt` value is the first number, from 1 to 100000. The default is 1. A second call gives the status 409. An agent token cannot set the prefix.

## Write the draft

1. Select **New**. The **New promise** dialog opens.
2. Enter a **Title**.
3. Enter the **Claim**. Write one statement that a check can show to be true or false.
4. Enter the **Scope**, the **Preconditions**, the **Expected outcome** and the **Forbidden outcome**.
5. In **What counts as evidence**, say what result you accept.
6. Below **Linked to**, select the scenarios, operations, channels, subscriptions, messages and elements that the promise is about. A group shows only when the model has items of that kind.
7. Select **Create draft**.

Vellum gives the draft the next requirement ID. The ID never changes and is never used again.

**Create draft** needs a **Title** and a **Claim**. Approval also needs the **Preconditions**, the **Expected outcome**, the **Forbidden outcome** and **What counts as evidence**. The **Scope** is optional.

A draft is not a requirement. The SDD and the brief list it as not approved.

## Add obligations

An obligation is one check that gives evidence for the promise. A promise with no obligation has no evidence.

1. Select the draft in the **Promises** list.
2. If you changed a field, select **Save draft**. **Generate from the links** is not available while a change is not saved.
3. Select **Generate from the links**. Vellum calls no AI for this.
4. For each test plan, enter the repository as `owner/name` in lower case, the workflow file or name and, if needed, the job. Then select **Set source**. A test plan with no evidence source can get no result.

The repository of an evidence source must be one that the project imports: it has an accepted import or a tracked ref. The system is always `github-actions`. You can set the source only in a draft. After approval, start a new revision to set it.

**Generate from the links** adds these obligations.

| Link | Obligation |
|---|---|
| A scenario that has an assertion | A `scenario-check` that plays the scenario with its own faults and expects its own assertions. |
| A subscription | A `failure-test` plan for redelivery, duplicate delivery and the dead-letter path. |
| An operation | A `contract-test` plan. |
| The `requiresPattern` field of the promise | A `source-pattern-check`. |

A scenario with no assertion gives no obligation, unless the promise has a `forbiddenEvents` entry. If you generate again, Vellum adds only the obligations that the draft does not hold. It keeps an obligation that you edited.

The app has no field for `forbiddenEvents` or `requiresPattern`, and no editor for the faults and assertions of an obligation. Set them over HTTP:

- `PUT /api/projects/{projectId}/promises/{promiseId}` replaces the text and the links of the draft, with `forbiddenEvents` and `requiresPattern`.
- `POST /api/projects/{projectId}/promises/{promiseId}/obligations` adds an obligation. The body has `kind`, `title` and `recipe`.
- `PUT /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}` changes an obligation. The `obligationKey` is the last part of the obligation ID, such as `O2`.
- `DELETE /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}` removes an obligation.

An obligation changes only in a draft. [Promises and evidence](/reference/promises-and-evidence/) lists the kinds of obligation.

## Approve

1. Read the draft.
2. Select **Approve revision 1**. The label holds the number of the draft.

Only a workspace owner approves, and only in the app. An agent token cannot approve, whatever scopes it has. It gets the status 403 and the type `human_only`.

The approval binds the text that you read. The request sends the `revision` and the `contentHash` of the draft. If the draft changed after you opened it, Vellum refuses the approval with the status 409 and the type `revision_changed`. The app then shows the draft as it is now.

If a required field is empty, Vellum refuses the approval and the dialog names the field.

After approval, the text and the obligations of the revision do not change.

## Check the promise

1. Select the promise in the **Promises** list.
2. Select **Run** next to a `scenario-check` or a `source-pattern-check`.
3. For a test plan, run the test in your CI. Then enter the URL of the workflow run, and optionally the commit, and select **Record run**.

**Record run** shows on an approved revision whose plan has an evidence source. Vellum reads the run from GitHub and compares the repository, the workflow, the job and the commit. If Vellum cannot check the run, it stores a claim. A claim counts as not run.

In the app, **Run** on a `source-pattern-check` sends no scan, so the result is `not-run`. To run it, call `POST /api/projects/{projectId}/promises/{promiseId}/obligations/{obligationKey}/run` with the `scanId` of a completed scan of the repository.

A run on a draft is labelled as a draft run. It does not count after approval. Run the obligation again on the approved revision.

A result holds for the inputs that the run read. When an input changes, the result shows `(stale)` with the list of changes, and it is no longer counted as passed or failed. Run the obligation again. See [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/).

The promise shows the result of each obligation and a list of **Evidence gaps**. Vellum never shows a promise as verified. Read the counts and the gaps.

## Change an approved promise

1. Select the promise in the **Promises** list.
2. Select **Start a new revision**.
3. Edit the draft and approve it.

The earlier revision stays in force until an owner approves the new one. It stays in the history. A result of the earlier revision does not count for the new one.

You can delete a promise that was never approved, with `DELETE /api/projects/{projectId}/promises/{promiseId}`. Vellum does not delete an approved promise. To retire it, call `POST /api/projects/{projectId}/promises/{promiseId}/revise` with `"retire": true`, then approve that revision. The app has no control for delete or retire.

## Let an agent propose

An agent with the `propose` scope can write a draft. The origin of a promise that an agent creates is `agent-suggested`, whatever the request says. You approve it, or you do not. See [Connect a coding agent](/how-to/connect-a-coding-agent/).

## Limits

| Limit | Value |
|---|---|
| Promises in a project | 2,000 |
| Revisions of a promise | 100 |
| Obligations in a revision | 50 |
| Links of one kind | 50 |
| Content and obligations of one revision, as JSON | 262,144 characters. A larger write gives the status 413 and the type `promise_too_large`. |
| Runs of one obligation of one revision | 1,000 |
| Runs and evidence records of one caller | 120 in a minute. More gives the status 429. |

## Next

- [Run the promise loop](/tutorials/promise-loop/)
- [Promises and evidence: the concept](/concepts/promises-and-evidence/)
- [Promises and evidence: the reference](/reference/promises-and-evidence/)
- [Export for an agent](/how-to/export-for-an-agent/)
- [Use the HTTP API](/how-to/use-the-http-api/)
