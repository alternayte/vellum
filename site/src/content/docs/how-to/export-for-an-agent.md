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

The app exports the main model at its version now. To export a named revision or a branch, use HTTP or an agent.

## Export a brief in the app

A brief holds only the part of the model that one change needs.

1. Open **Build** in the left toolbar.
2. Below **Brief for an agent**, select the scope.
   - **A promise**: the brief holds the promise, what it links and its checks.
   - **A change**: the brief holds what the impact of one scan names, and the impact report of that scan.
   - **Elements**: the brief holds the elements that you select, with their operations, subscriptions and messages. The elements that are selected on the canvas are ticked.
3. Select **Preview the brief**.
4. Read the first box. It says **This scope is build ready**, or **This scope is not build ready** with each blocker.
5. Select **Copy** or **Download**.

A promise that is not approved is not a requirement. The preview names it, and the brief lists it under "do not implement".

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

The answer has these headers.

| Header | What it says |
|---|---|
| `X-Vellum-Build-Ready` | `true` when the exported state has no open blocker. |
| `X-Vellum-Current` | `true` when each input is still at its pinned version. `false` means that the export is of a past state. |
| `X-Vellum-Bundle-Hash` | The hash of the files of the bundle. |
| `X-Vellum-Stream-Version` | The version of the model that the export read. |

## Export a pinned state again

A manifest names the version of each input in its `pinned` member. Vellum can make the export of that state again, also after the project changed.

1. Read the manifest. Use `GET /api/projects/$PROJECT/build/manifest`, or the `manifest.json` of a bundle.
2. Send its `pinned` member in the body: `{"format":"bundle","pinned":{...}}`. Do not send `revisionId` or `branchId` with it.

Send the `pinned` member whole and unchanged. It holds a `seal`. The seal says that this Vellum instance issued this state for this project. Vellum makes the seal with a key that it derives from the master key. The seal is not a secret.

Vellum refuses a pinned state that it did not issue. The answer has the status 409 and the type `pinned_not_issued`. This happens in these cases.

- The state has no seal. A manifest of a format before `vellum-bundle/4` has none.
- A member of the state changed after Vellum issued it. For example, a promise or a commit was removed or added.
- The seal is of another project.
- The seal is of another Vellum instance.
- The seal is of a master key that is not loaded any more.

If Vellum refuses the state, read the manifest again and send its `pinned` member. Or send the revision or the branch.

A pinned state can be a past state. Its bundle then says what was true then. For example, a state from before a blocker opened says that it is build ready. Read `X-Vellum-Current` to know whether the state is the state of now. The SDD starts with the model version that it is of.

The seal is in `manifest.json` only. It does not change a file hash or the bundle hash. On one instance with one master key, the same state gives the same zip, byte for byte.

### After a master key rotation

A seal names the master key that made it. While the old key is loaded as the previous key, Vellum accepts a seal of the old key. The new export has a seal of the new key. Its `manifest.json` and its zip then differ from the old ones in the seal only. The bundle hash is the same.

After you remove the previous key, Vellum refuses a seal of that key. Export each pinned state that you want to keep before you remove the previous key. The check of a bundle does not need the seal, so it works after a rotation and for a bundle of another instance's key.

## Export a part of the model

An export that is too large is not cut. A bundle holds at most 33,554,432 bytes, 16,777,216 bytes in one file and 5,000 files. A larger export gives the status 413 and the type `bundle_too_large`. Export a brief for a part of the model.

A brief holds the part of the model that one change needs. For a brief, the endpoint is `POST /api/projects/{projectId}/build/brief`.

```sh
curl -X POST "http://localhost:8080/api/projects/<project-id>/build/brief" \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"promiseIds":["<promise-id>"]}'
```

The body takes `promiseIds`, `scenarioIds` and `elementIds`, and optionally `revisionId` or `branchId`. A promise ID is the ID of the promise, not its requirement ID. Select at least one ID and at most 200. The body also takes `changeScanId`, the ID of a completed scan. The brief then selects what the change impact of that scan names and holds the impact report.

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

The answer has a third value, `issued`. It is `true` when the pinned state has a seal that this instance issued for this project. Only such a state can be exported again. A bundle from before the seal gives `false`. That does not make the bundle wrong.

The check does not say that a state is build ready. Treat `buildReady` in a manifest as the state of the project now only when `matchesPinned` and `current` are both `true`.

With an agent, call `verify_bundle_manifest` with the `manifest.json` of the bundle. The result has `matchesPinned`, `current`, `moved` and `issued`. A manifest alone does not show a change to a file. A message to the MCP endpoint holds at most 1,048,576 bytes. To check the files, or a larger manifest, send the zip to `POST /api/projects/{projectId}/build/verify`:

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

Vellum writes the format `vellum-bundle/4`. Its manifest holds the seal of the pinned state. The other files are the files of the format `vellum-bundle/3`.

A bundle of the format `vellum-bundle/3` has no seal. A bundle of the format `vellum-bundle/2` has no seal, no review and no decision. Vellum still checks both: the check generates the bundle again as it was.

Vellum does not export a pinned state of those formats. `POST build/export` with a `pinned` state that names no `decisions` gives the status 409 and the type `pinned_format_not_exported`. A bundle with no review has no review blocker, so it must not say that a state is build ready. A `pinned` state with `decisions` and no seal gives the status 409 and the type `pinned_not_issued`. Export again in the format `vellum-bundle/4`: send the revision or the branch, or the `pinned` member of `GET build/manifest`.

## What the agent does next

The agent builds to the contracts and the approved promises. It cites each requirement ID in its commits and tests. It records the result of its tests as evidence. See [Connect a coding agent](/how-to/connect-a-coding-agent/).

## See also

- [Bundle and SDD format](/reference/bundle-and-sdd-format/)
- [MCP tools](/reference/mcp-tools/)
- [Use the HTTP API](/how-to/use-the-http-api/)
- [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/)
- [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/)
- [Run the promise loop](/tutorials/promise-loop/)
