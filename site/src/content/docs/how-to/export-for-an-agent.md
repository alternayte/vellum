---
title: Export for an agent
description: Export the SDD, the bundle or a brief, and check that a bundle is unchanged and current.
---

This guide shows you how to give a coding agent what it builds from. You need the viewer role. An agent token needs the `export` scope.

## Select what to export

| Export | What it holds | Use it when |
|---|---|---|
| SDD | One Markdown file: the elements, the contracts, the scenarios, the promises, the slices and the blockers. | An agent builds or reviews the whole system. |
| Bundle | A zip: `SDD.md`, the OpenAPI and AsyncAPI documents, `architecture.json`, a file for each scenario and each promise, and `manifest.json`. | You hand the work to another team or keep a record. |
| Brief | One Markdown file for a selection of promises, scenarios and elements. | An agent makes one change. |

Each export is of a pinned state. The same state gives the same bytes.

## Export in the app

1. Open **Build** in the left toolbar.
2. Read **Blockers**. A blocker is an open point, such as a promise that is not approved or a fact that is inferred.
3. Select **SDD.md** or **Bundle**.

You can export when blockers are open. The SDD lists every blocker, so an agent does not treat an open point as settled.

## Export with an agent

An agent calls these MCP tools.

- `export_sdd` returns the SDD. It needs the `export` scope.
- `export_brief` returns a brief for the promise, scenario and element IDs you give. It needs the `export` scope.
- `get_build_manifest` returns the state of the build: ready or not, the blockers, the pinned versions and the file hashes. It needs the `read` scope.

A tool result holds at most 100,000 bytes. A larger SDD or brief is not cut. The result then holds the manifest, and its `notice` names the HTTP endpoint that gives the document.

## Export over HTTP

```sh
curl -X POST https://vellum.example.com/api/projects/$PROJECT/build/export \
  -H "Authorization: Bearer vlm_agt_..." -H "Content-Type: application/json" \
  -d '{"format":"sdd"}' -o SDD.md
```

Use `"format":"bundle"` for the zip. To export a named revision, add `"revisionId"`.

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

For a brief:

```sh
curl -X POST https://vellum.example.com/api/projects/$PROJECT/build/brief \
  -H "Authorization: Bearer vlm_agt_..." -H "Content-Type: application/json" \
  -d '{"promiseIds":["<promise ID>"]}'
```

The answer has `brief`, the Markdown, and `manifest`. In the manifest, `notApproved` lists promises that are not requirements, and `unconfirmedFacts` lists facts that are inferred, unknown or stale.

## Check a bundle

A check answers two questions.

- **Is the bundle what its pinned versions generate?** If not, somebody changed the bundle after the export.
- **Are those versions still the newest?** If not, the model moved after the export. That is drift, not a changed bundle.

In the app:

1. Open **Build**.
2. Select **Check a bundle**.
3. Select the zip in **Bundle file**.

The answer has a third value, `issued`. It is `true` when the pinned state has a seal that this instance issued for this project. Only such a state can be exported again. A bundle from before the seal gives `false`. That does not make the bundle wrong.

The check does not say that a state is build ready. Treat `buildReady` in a manifest as the state of the project now only when `matchesPinned` and `current` are both `true`.

With an agent, call `verify_bundle_manifest` with the `manifest.json` of the bundle. The result has `matchesPinned`, `current`, `moved` and `issued`. A manifest alone does not show a change to a file. To check the files, send the zip over HTTP:

```sh
curl -X POST https://vellum.example.com/api/projects/$PROJECT/build/verify \
  -H "Authorization: Bearer vlm_agt_..." -H "Content-Type: application/zip" \
  --data-binary @vellum-bundle.zip
```

### A bundle of an older format

Vellum writes the format `vellum-bundle/4`. Its manifest holds the seal of the pinned state. The other files are the files of the format `vellum-bundle/3`.

A bundle of the format `vellum-bundle/3` has no seal. A bundle of the format `vellum-bundle/2` has no seal, no review and no decision. Vellum still checks both: the check generates the bundle again as it was.

Vellum does not export a pinned state of those formats. `POST build/export` with a `pinned` state that names no `decisions` gives the status 409 and the type `pinned_format_not_exported`. A bundle with no review has no review blocker, so it must not say that a state is build ready. A `pinned` state with `decisions` and no seal gives the status 409 and the type `pinned_not_issued`. Export again in the format `vellum-bundle/4`: send the revision or the branch, or the `pinned` member of `GET build/manifest`.

## What the agent does next

The agent builds to the contracts and the approved promises. It cites each requirement ID in its commits and tests. It records the result of its tests as evidence. See [Connect a coding agent](/how-to/connect-a-coding-agent/).
