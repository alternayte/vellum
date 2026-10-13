---
title: Export for an agent
description: Export the SDD, the bundle or a brief, and check that a bundle is unchanged and current.
---

This guide shows you how to give a coding agent what it builds from. You need the viewer role. An agent token needs the `export` scope.

## Select what to export

| Export | What it holds | Use it when |
|---|---|---|
| SDD | One Markdown file with 12 sections: the overview, the elements, the operations, the messaging contracts, the schemas, the data stores, the scenarios, the promises, the failure invariants, the slices, the acceptance tests and the blockers. | An agent builds or reviews the whole system. |
| Bundle | A zip: `SDD.md`, `architecture.json`, one `openapi/<element ID>.yaml` for each element that has an OpenAPI document, `asyncapi.yaml`, one file for each scenario, each promise and each accepted decision, and `manifest.json`. | You hand the work to another team or keep a record. |
| Brief | One Markdown file for a selection of promises, scenarios and elements. | An agent makes one change. |

Each export is of a pinned state. The same state gives the same bytes.

In a bundle, an approved promise is in `promises/`. A promise that is not approved is in `promises-not-approved/`. A retired promise is in `promises-retired/`.

## Export in the app

1. Open **Build** in the left toolbar.
2. Read **Blockers**. A blocker is an open point, such as a promise that is not approved or a fact that is inferred. The list shows only when a blocker is open.
3. Select **SDD.md** or **Bundle**.

You can export when blockers are open. The SDD lists every blocker, so an agent does not treat an open point as settled.

The app exports the main model at its version now. To export a named revision or a branch, use HTTP or an agent. The app has no control for a brief.

## Export with an agent

An agent calls these MCP tools.

- `export_sdd` returns the SDD. It needs the `export` scope.
- `export_brief` returns a brief for the promise, scenario and element IDs you give. It needs the `export` scope.
- `get_build_manifest` returns the state of the build: ready or not, the blockers, the pinned versions and the file hashes. It needs the `read` scope.

A tool result holds at most 100,000 bytes. A larger SDD or brief is not cut. The result then holds the manifest, and its `notice` names the HTTP endpoint that gives the document.

No MCP tool returns the bundle zip. Get the zip over HTTP.

## Export over HTTP

The endpoint is `POST /api/projects/{projectId}/build/export`.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/build/export" \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"format":"sdd"}' -o SDD.md
```

Use `"format":"bundle"` for the zip. The default is `bundle`. To export a named revision, add `"revisionId"`. To export a draft or a variant, add `"branchId"`. Give one of the two, not both. To export one state again, send the `pinned` object of a manifest and no `revisionId` or `branchId`.

The answer has the headers `X-Vellum-Stream-Version`, `X-Vellum-Bundle-Hash` and `X-Vellum-Build-Ready`.

An export that is too large is not cut. A bundle holds at most 33,554,432 bytes, 16,777,216 bytes in one file and 5,000 files. A larger export gives the status 413 and the type `bundle_too_large`. Export a brief for a part of the model.

For a brief, the endpoint is `POST /api/projects/{projectId}/build/brief`.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/build/brief" \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"promiseIds":["<promise-id>"]}'
```

The body takes `promiseIds`, `scenarioIds` and `elementIds`, and optionally `revisionId` or `branchId`. A promise ID is the ID of the promise, not its requirement ID. Select at least one ID and at most 200.

The answer has `brief`, the Markdown, and `manifest`. In the manifest, `notApproved` lists promises that are not requirements, `retired` lists selected promises that are retired, and `unconfirmedFacts` lists facts that are inferred, unknown or stale.

To read the manifest of the bundle and write no file, call `GET /api/projects/{projectId}/build/manifest`. A token with the `read` scope can call it.

## Check a bundle

A check answers two questions.

- **Is the bundle what its pinned versions generate?** If not, somebody changed the bundle after the export.
- **Are those versions still the newest?** If not, the model moved after the export. That is drift, not a changed bundle.

In the app:

1. Open **Build**.
2. Go to **Check a bundle**.
3. Select **Select a bundle file** and select the zip.

The app shows if the files match, each file that differs, and the number of inputs that moved.

With an agent, call `verify_bundle_manifest` with the `manifest.json` of the bundle. The result has `matchesPinned`, `current` and `moved`. A manifest alone does not show a change to a file. A message to the MCP endpoint holds at most 1,048,576 bytes. To check the files, or a larger manifest, send the zip to `POST /api/projects/{projectId}/build/verify`:

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/build/verify" \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/zip" \
  --data-binary @vellum-bundle.zip
```

The endpoint also takes a JSON body with one member, `manifest`.

| Field | Meaning |
|---|---|
| `matchesPinned` | `true` when each file and the manifest are what the pinned versions generate. |
| `identical` | `true` when `matchesPinned` is `true` and the bytes of the zip are the bytes that Vellum writes. |
| `manifest` | `match` or `differs`. `manifestDifferences` names the fields that differ. |
| `archive` | `match` or `differs` for a zip. `null` for a manifest alone. |
| `files` | One entry for each file, with the `status` `match`, `differs`, `missing` or `unknown-file`. |
| `current` | `true` when no input moved after the export. |
| `moved` | Each input that moved, with its `kind`, `id`, `pinned` version and `current` version. |

A zip that another tool wrote again can have `matchesPinned` `true` and `identical` `false`. The files in it are unchanged.

Vellum refuses a body that is not a bundle it writes with the status 400 and the type `invalid_bundle`. Examples are a zip with an entry that the manifest does not list, or with one entry name two times. If a pinned version is no longer stored, the status is 409 and the type is `pinned_input_unavailable`.

### A bundle of an older format

A bundle of the format `vellum-bundle/2` holds no review and no decision. Vellum still checks such a bundle: the check generates it again as it was. Vellum cannot generate a manifest of an earlier format again.

Vellum does not export the format `vellum-bundle/2` any more. `POST /api/projects/{projectId}/build/export` with a `pinned` state that names no `decisions` gives the status 409 and the type `pinned_format_not_exported`. A bundle with no review has no review blocker, so it must not say that a state is build ready. Export again in the format `vellum-bundle/3`: send the revision or the branch, or the `pinned` state of a `vellum-bundle/3` manifest.

## What the agent does next

The agent builds to the contracts and the approved promises. It cites each requirement ID in its commits and tests. It records the result of its tests as evidence. See [Connect a coding agent](/how-to/connect-a-coding-agent/).

## See also

- [Bundle and SDD format](/reference/bundle-and-sdd-format/)
- [MCP tools](/reference/mcp-tools/)
- [Use the HTTP API](/how-to/use-the-http-api/)
- [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/)
- [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/)
- [Run the promise loop](/tutorials/promise-loop/)
