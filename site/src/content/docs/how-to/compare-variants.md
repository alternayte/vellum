---
title: Save revisions and compare variants
description: Save a named revision of the model, fork a variant from main or from a revision, and compare the cost and the stress result of two variants.
---

This guide shows you how to save a revision of the model, how to make a variant and how to compare two variants. You need the editor role.

## Know the three words

| Word | Meaning | Can it change? |
|---|---|---|
| Main | The model of the project. | Yes. |
| Revision | A named copy of main at one version. | No. |
| Variant | A second model that starts as a copy of main or of a revision. It has its own cost sheet. | Yes. |

A variant is for an alternative that you want to keep next to main. A draft is for a change that you want to merge into main. See [Work in a draft](/how-to/work-in-a-draft/).

## Know the limits

The `curl` examples use the session cookie of a signed-in person in the file `vellum.cookies`. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in. An agent token cannot make the write calls on this page: those endpoints are not open to agent tokens and answer `403`.

- The app has no control for revisions. Use the API.
- The app creates a variant only in the **Costs** flyout.
- The canvas shows main only. It cannot show a variant.
- Vellum has no general comparison of two models. It compares the cost estimates and the stress runs of two variants.
- Vellum cannot merge a variant into main.
- Vellum has no endpoint that deletes or closes a variant.

## Save a revision

1. Send the request. The `id` is a new UUID that you make. The `note` is optional text.

   ```sh
   curl -X POST "http://localhost:8080/api/projects/<project-id>/revisions" \
     -b vellum.cookies \
     -H "Content-Type: application/json" \
     -d '{"id":"<new-uuid>","name":"before-outbox","note":"The model before the outbox change"}'
   ```

2. Read the answer. `version` is the version of main that the revision holds.

`GET /api/projects/{projectId}/revisions` lists the revisions. `GET /api/projects/{projectId}/revisions/{revisionId}` gives one.

A revision name is unique in the project. A second revision with the same name gets `409` with `A revision with this name already exists`. A request with an `id` that the project already has gives the stored revision and changes nothing.

Two endpoints read a revision instead of main when you add `?revisionId=<revision-id>`:

- `GET /api/projects/{projectId}/review/findings`
- `GET /api/projects/{projectId}/build/manifest`

## Make a variant in the app

1. Select **Costs** in the left toolbar.
2. Below **Branch**, select **New variant from main**.
3. Enter a **Variant name**.
4. Select **Create**.

The variant forks the model of main. Its cost sheet starts as a copy of the sheet that the flyout shows. Vellum makes the variant and the copy together: both exist, or neither exists. If the request fails, the flyout shows **No variant was created.** and the reason.

The **Branch** list then shows the variant. It also shows the open drafts.

## Make a variant from a revision

The app always forks from main. To fork from a revision, use the API.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/variants" \
  -b vellum.cookies \
  -H "Content-Type: application/json" \
  -d '{"id":"<new-uuid>","name":"with-queue","baseRevisionId":"<revision-id>","costSheetFrom":"main"}'
```

| Field | Value |
|---|---|
| `id` | A new UUID. |
| `name` | Unique in the project. |
| `baseRevisionId` | The revision to copy, or `null` to copy main as it is now. |
| `costSheetFrom` | Optional. `main`, or the `streamId` of an open draft or variant. The variant copies that cost sheet. When you leave it out, the variant copies the sheet of the branch that the model comes from. |

The answer has `streamId`. That value is the branch ID of the variant. Keep it.

`GET /api/projects/{projectId}/variants` lists the variants. `GET /api/projects/{projectId}/variants/{variantId}` gives one.

## Change a variant

The model endpoints accept `?branchId=<streamId>`. With it, the request reads or writes the variant and not main.

```sh
curl "http://localhost:8080/api/projects/<project-id>/elements?branchId=<stream-id>" \
  -b vellum.cookies
```

The endpoints for elements, relationships, messages, operations, channels, subscriptions and bindings accept `branchId`. So do the cost endpoints, the scenario run, the stress run, the review findings and the build manifest.

A `branchId` that is not an open branch of the project gets `400` with the error type `invalid_branch`.

In the app, select the variant in the **Branch** list of the **Costs** flyout to edit its cost sheet.

## Compare the cost of two variants

1. Select **Costs**, then **Lines, saved estimates and compare**.
2. Select **Compare**.
3. Select a branch for **Left** and a different branch for **Right**.

Vellum says which side costs less only when the two sides can be compared. In all other cases it shows **Not comparable.** and the reason. An empty cost sheet is not a cost of zero.

The endpoint is `GET /api/projects/{projectId}/costs/compare` with `left` and `right`. Each value is `main` or the `streamId` of an open draft or variant. [Estimate costs](/how-to/estimate-costs/#compare-two-variants) has the details.

## Compare the stress result of two variants

1. Run the same stress profile on each variant. Add `?branchId=<streamId>` to `POST /api/projects/{projectId}/stress/profiles/{profileId}/runs`.
2. Call `GET /api/projects/{projectId}/stress/compare` with the two run IDs as `left` and `right`.

The answer lists the inputs that differ and the difference of each metric. It gives no verdict. See [Stress a flow](/how-to/stress-a-flow/#compare-two-runs).

Two more comparisons use measured load-test results. See [Load test with k6](/how-to/load-test-with-k6/).
