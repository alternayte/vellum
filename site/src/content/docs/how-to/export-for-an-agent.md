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

With an agent, call `verify_bundle_manifest` with the `manifest.json` of the bundle. The result has `matchesPinned`, `current` and `moved`. A manifest alone does not show a change to a file. To check the files, send the zip over HTTP:

```sh
curl -X POST https://vellum.example.com/api/projects/$PROJECT/build/verify \
  -H "Authorization: Bearer vlm_agt_..." -H "Content-Type: application/zip" \
  --data-binary @vellum-bundle.zip
```

### A bundle of an older format

A bundle of the format `vellum-bundle/2` holds no review and no decision. Vellum still checks such a bundle: the check generates it again as it was.

Vellum does not export that format any more. `POST build/export` with a `pinned` state that names no `decisions` gives the status 409 and the type `pinned_format_not_exported`. A bundle with no review has no review blocker, so it must not say that a state is build ready. Export again in the format `vellum-bundle/3`: send the revision or the branch, or the `pinned` state of a `vellum-bundle/3` manifest.

## What the agent does next

The agent builds to the contracts and the approved promises. It cites each requirement ID in its commits and tests. It records the result of its tests as evidence. See [Connect a coding agent](/how-to/connect-a-coding-agent/).
