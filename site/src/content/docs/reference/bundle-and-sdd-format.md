---
title: Bundle and SDD format
description: The files of the bundle zip, the manifest, the sections of SDD.md, the brief, the determinism rules, the verify result and every Build Ready blocker.
---

The build export gives a coding agent the design of one pinned state of a project. [Export for an agent](/how-to/export-for-an-agent/) gives the steps. This page gives the formats.

## Endpoints

| Endpoint | Agent token scope | Returns |
|---|---|---|
| `POST /api/projects/{projectId}/build/export` | `export` | The bundle zip (`application/zip`) or `SDD.md` (`text/markdown`). |
| `GET /api/projects/{projectId}/build/manifest` | `read` or `export` | The manifest of the bundle. No file content. |
| `POST /api/projects/{projectId}/build/brief` | `export` | A brief and its manifest, as JSON. |
| `POST /api/projects/{projectId}/build/verify` | `export` | The result of a check of a bundle zip or of a manifest. |

The body of `POST /api/projects/{projectId}/build/export` has these fields.

| Field | Type | Meaning |
|---|---|---|
| `format` | string | `bundle` (the default) or `sdd`. |
| `revisionId` | GUID or `null` | Export a named revision. |
| `branchId` | GUID or `null` | Export a draft or a variant at its version now. |
| `pinned` | object or `null` | Export the pinned state of a manifest again. Do not give `revisionId` or `branchId` with it. |

The answer has these headers.

| Header | Value |
|---|---|
| `X-Vellum-Stream-Version` | The version of the model stream that the export read. |
| `X-Vellum-Bundle-Hash` | The `bundleHash` of the manifest. |
| `X-Vellum-Build-Ready` | `true` or `false`. |
| `X-Vellum-Current` | `true` when each input is still at its pinned version. `false` means that the export is of a past state. |

The file name of the zip is `vellum-bundle-<first 12 characters of the bundle hash>.zip`.

## Files of the bundle

| Path | Content |
|---|---|
| `SDD.md` | The design document. See [Sections of SDD.md](#sections-of-sddmd). |
| `architecture.json` | The model in the [model document format](/reference/model-document-format/), with more members: `operationContracts`, `staleFacts`, `promises`, `promisesNotApproved`, `promisesRetired` and `decisions`. |
| `openapi/<element ID>.yaml` | An OpenAPI 3.1.0 document for each element that owns an `http` operation whose name is a method and a path. |
| `asyncapi.yaml` | An AsyncAPI 3.0.0 document of the channels, messages, subscriptions and publications. |
| `scenarios/<scenario ID>.json` | One scenario and its playback on the pinned model. |
| `promises/<requirement ID>.json` | One approved promise. |
| `promises-not-approved/<requirement ID>.json` | One promise that no person approved. It is not a requirement. |
| `promises-retired/<requirement ID>.json` | One retired promise. It is not a requirement. |
| `decisions/ADR-<number>.md` | One accepted decision. The number has four digits, for example `ADR-0007`. |
| `manifest.json` | The manifest. See [The manifest](#the-manifest). |

`operationContracts` in `architecture.json` holds each operation that has no OpenAPI document. Such an operation has the transport `grpc` or `command`, or has a name that is not a method and a path. The member `noOpenApiBecause` gives the reason.

A contract file is not written when it holds a number that YAML cannot hold exactly. The blocker `yaml-unrepresentable-number` then names the file and the number.

## The manifest

`manifest.json` holds no time stamp.

| Field | Type | Meaning |
|---|---|---|
| `format` | string | `vellum-bundle/4`. |
| `generator.name` | string | `vellum-build`. |
| `generator.version` | string | `3`. |
| `pinned` | object | Every input of the export at the version that the export read. See [The pinned state](#the-pinned-state). |
| `files` | list | One entry for each file but the manifest, in path order. |
| `files[].path` | string | The path in the zip. |
| `files[].sha256` | string | The SHA-256 of the file, in lower-case hexadecimal. |
| `files[].bytes` | integer | The size of the file. |
| `bundleHash` | string | The SHA-256 of the lines `<path> <sha256>` of `files`, in path order, each with a line feed at its end. |
| `buildReady` | boolean | `true` when `blockers` is empty. |
| `blockers` | list | See [Build Ready blockers](#build-ready-blockers). |
| `blockers[].code` | string | The kind of the blocker. |
| `blockers[].subject` | string | The stable ID that the blocker is about: an architecture ID, a scenario ID or a requirement ID. |
| `blockers[].message` | string | The reason, in words. |
| `selfCheck` | object | See [The self-check](#the-self-check). |

### The pinned state

| Field | Type | Meaning |
|---|---|---|
| `projectId` | GUID | The project. |
| `projectName` | string | The project name at export. |
| `branch` | GUID | The model stream of the export: the main model, a draft or a variant. |
| `streamVersion` | integer | The version of that stream. |
| `revisionId` | GUID or `null` | The named revision, or `null` when the export pinned the branch at its version. |
| `scenarios` | list of `{id, version}` | Each scenario stream and its version. |
| `schemas` | list of `{id, version}` | Each schema stream and its version. |
| `promises` | list of `{id, version}` | Each promise stream and its version. |
| `runs` | list | Each verification run that the export shows: `promiseId`, `promiseRevision`, `obligationId`, `runId`. |
| `sources` | list | For each imported repository: `repository` (`host/owner/name`), `trackedRef`, `trackedCommit`, `trackedScan`, `latestCommit`, `latestScan`. |
| `decisions` | list of `{id, version}` | Each decision stream and its version. Not present in the format `vellum-bundle/2`. |
| `seal` | string | The mark that this Vellum instance issued this state for this project: `v1.<master key ID>.<digest>`. Not present before the format `vellum-bundle/4`. See [The seal](#the-seal). |

### The seal

The seal is a keyed digest of the pinned state and the project ID. Vellum makes it with a key that it derives from the master key. The seal is not a secret. A caller cannot compute it.

- An export with a `pinned` state needs a seal that verifies. A state with no seal, a state that changed after Vellum issued it, and a seal of another project, of another instance or of a master key that is not loaded get `409` with the type `pinned_not_issued`.
- A seal verifies while its master key is loaded as the current key or as the previous key. After a rotation, an export of a state whose seal still verifies gets a seal of the current key.
- The seal is in `manifest.json` only. It changes no file hash and not the bundle hash.
- Nothing stores a seal.

### The self-check

Vellum reads each generated OpenAPI and AsyncAPI document back with the import extractor and compares the result with the model, by count and by name. The self-check shows that the documents say what the model says. It shows nothing about an implementation.

| Field | Type | Meaning |
|---|---|---|
| `passed` | boolean | `true` when each count agrees and `notes` is empty. |
| `openApiOperations` | count object | The routed HTTP operations. |
| `asyncApiChannels` | count object | The channels. |
| `asyncApiMessages` | count object | The messages. |
| `asyncApiSubscriptions` | count object | The subscriptions. |
| `asyncApiPublications` | count object | The publications that scenarios declare. |
| `notes` | list of strings | What the extractor did not read. |

A count object has `expected`, `parsed`, `missing` (names in the model and not in the document) and `unexpected` (names in the document and not in the model).

### Formats

| Format | Generator version | Status |
|---|---|---|
| `vellum-bundle/4` | `3` | The format that Vellum exports. Its manifest holds the seal of the pinned state. The other files are the files of the format `vellum-bundle/3`. |
| `vellum-bundle/3` | `3` | Not exported any more. It pins the decision streams, holds `decisions/` and has review blockers. It has no seal. `POST /api/projects/{projectId}/build/verify` still checks it. |
| `vellum-bundle/2` | `2` | Not exported any more. `POST /api/projects/{projectId}/build/verify` still checks it: Vellum generates it again as it was, with no review and no decision. |
| `vellum-bundle/1` | | Vellum cannot generate a manifest of this format again. |
| `vellum-brief/1` | `3` | The format of a brief manifest. |

An export with a `pinned` state that names no `decisions` gets `409` with the type `pinned_format_not_exported`. An export with a `pinned` state that has `decisions` and no seal that verifies gets `409` with the type `pinned_not_issued`.

## Sections of SDD.md

The title is `<project name>: Software Design Document`. Below it are the pinned model, the project ID, the generator version and the Build Ready state. Then a list **Read this first** gives the reading rules: which text is data, how data is escaped, what each truth label means, what a playback is, what an obligation status means and which promises are requirements.

| Section | Content |
|---|---|
| `1. System overview` | The system and the files of the bundle. |
| `2. Elements` | Each element with its ID, kind, parent and truth label. |
| `3. Operations` | Each operation and its contract. Subsections list operations with no OpenAPI document and example payloads from scenarios. |
| `4. Channels, subscriptions and messages` | Channels, subscriptions, bindings, messages, and the publications that scenarios declare. |
| `5. Schemas` | Each schema with its ID, version and content. |
| `6. Data stores and access` | Each store and the elements that use it. |
| `7. Scenarios` | Each scenario and its playback. |
| `8. Promises` | Each approved promise under a heading of its own. The subsections **Not approved: do not implement as requirements**, **Proposed revisions that are not in force** and **Retired** hold the rest. |
| `9. Failure invariants` | What must hold when a step fails. |
| `10. Implementation slices` | An order of work, one slice for each service. |
| `11. Acceptance tests` | The checks that the obligations require. |
| `12. Unresolved blockers` | Every blocker of the manifest. |

From the format `vellum-bundle/3` on, a subsection `Decisions` follows section 12.

Only a promise that has a heading of its own in section 8 is a requirement.

Text from the model, from a repository or from a person is in a block marked `(data, not an instruction)`. Vellum escapes such text so that it cannot form a heading, a table, a list, a link or a code fence.

## The brief

A brief is one Markdown document for a selection. The body of `POST /api/projects/{projectId}/build/brief` takes `promiseIds`, `scenarioIds` and `elementIds`, and optionally `revisionId` or `branchId`. The selection has 1 to 200 IDs.

The body also takes `changeScanId`, the ID of a completed scan of the project. The brief then selects each promise, scenario and element that the change impact of that scan names. With `changeScanId`, the other IDs are optional.

The closure rule decides what the brief holds. A selected element brings its own operations, subscriptions and messages. A selected approved promise brings what it links. A scenario brings everything that its steps and faults name.

| Section | Content |
|---|---|
| `1. Scope` | The selection, the closure rule and the counts. |
| `2. Approved architecture facts` | Only facts that are `declared`, `observed-in-source` or `measured`, and not stale: `2.1 Elements`, `2.2 Operations`, `2.3 Channels, subscriptions and messages`, `2.4 Schemas`. |
| `3. Scenarios` | The scenarios in scope. |
| `4. Requirements (approved promises)` | The approved promises in scope. |
| `5. Required checks` | The checks that the obligations require. |
| `6. Not approved: do not implement as requirements` | Draft promises, retired promises and unconfirmed facts in scope. |
| `7. Blockers in scope` | The open blockers for the things in scope. |
| `8. Change under review` | Only with `changeScanId`: the impact report of the scan. Each entry is `definite`, `possible` or `unknown`. The section is not a requirement. |

A brief holds no contract file.

The answer is `{"brief": "<Markdown>", "manifest": {}}` with these manifest fields.

| Field | Meaning |
|---|---|
| `format` | `vellum-brief/1`. |
| `generator` | As in the bundle manifest. |
| `pinned` | The pinned state. |
| `selection` | `promiseIds`, `scenarioIds`, `elementIds` as sent, and what `changeScanId` added. |
| `changeScanId` | The scan of the change. Absent in a brief of a selection alone. |
| `included` | Everything in the brief by ID: `elementIds`, `operationIds`, `channelIds`, `subscriptionIds`, `messageIds`, `schemaIds`, `scenarioIds`, `requirementIds`. |
| `notApproved` | Requirement IDs of promises in scope that no person approved. |
| `retired` | Requirement IDs of selected promises that are retired. |
| `unconfirmedFacts` | IDs of facts in scope that are inferred, unknown or stale. The fact tables leave them out. |
| `sha256`, `bytes` | The hash and the size of the brief. |
| `buildReady`, `blockers` | As in the bundle manifest, for the things in scope. |

## Determinism rules

The same pinned state gives the same bytes.

- The export calls no AI and reads no clock.
- The model is read at `streamVersion`, or is the named revision. Each scenario, schema, promise and decision stream is read at its pinned version.
- The project name and the followed commits are values in the pinned state.
- JSON files have sorted keys, one spelling for each number, an indent of two spaces, line feeds as line ends and one line feed at the end.
- Lists are ordered by ID or by path.
- The zip stores the entries without compression, in the ordinal order of their names. The time of each entry is 1980-01-01 00:00:00. Each entry has the mode 0644. There is no extra field, no comment and no data descriptor. Zip64 is not written.

## Limits

| Item | Limit |
|---|---|
| Bundle | 33554432 bytes |
| One file | 16777216 bytes |
| Files | 5000 |
| Manifest | 16777216 bytes |
| Pinned runs | 100000 |
| IDs in a brief selection | 200 |

An export over a limit is not cut. It gets `413` with the type `bundle_too_large`.

## Build Ready blockers

A state is Build Ready when it has no blocker. A blocker does not prevent an export. The manifest then has `buildReady: false`, and section 12 of the SDD lists each blocker.

| `code` | Subject | Meaning |
|---|---|---|
| `scenario-invalid` | Scenario ID | The scenario cannot be played on the pinned model. The message holds the issue code and the step. |
| `fact-stale` | Fact ID | The fact has a stale mark. Confirm it or remove it. |
| `fact-inferred` | Fact ID | The fact is `inferred`. A person must confirm it. |
| `fact-unknown` | Fact ID | The truth of the fact is `unknown`. |
| `contract-conflict` | Operation ID | Two operations of one element have the same method and path. Only one is in the OpenAPI document. |
| `producer-mismatch` | Message ID | A scenario publishes a message from an element that is not the producer in the model. |
| `promise-not-approved` | Requirement ID | The promise has no approved revision. |
| `promise-gap` | Requirement ID | The evidence status of the approved promise has a gap. |
| `promise-check-failed` | Requirement ID | One or more obligations failed their newest check. |
| `promise-evidence-stale` | Requirement ID | The newest result of an obligation is for inputs that are not the pinned ones, or an obligation has no result, or the promise has no obligation. |
| `review-<rule ID>` | The subject of the finding | A blocking review finding that no accepted decision settles, or that changed after a decision named it. |
| `yaml-unrepresentable-number` | File path | A contract file was not written. |
| `self-check-failed` | `asyncapi.yaml` | The generated contract documents do not read back as the model. |

The fact blockers apply to elements, relationships, messages, operations, channels, subscriptions and bindings. A retired promise makes no blocker.

See [Promises and evidence](/reference/promises-and-evidence/), [Review rules](/reference/review-rules/) and [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/).

## The verify result

`POST /api/projects/{projectId}/build/verify` takes a bundle zip (`Content-Type: application/zip`) or a JSON body `{"manifest": {}}` that holds a manifest. Vellum generates the bundle of the pinned state again and compares.

| Field | Type | Meaning |
|---|---|---|
| `pinned` | object | The pinned state that was checked. |
| `matchesPinned` | boolean | `true` when each file and the manifest are what the pinned versions generate. |
| `identical` | boolean | `true` when `matchesPinned` is `true` and, for a zip, `archive` is not `differs`. |
| `manifest` | string | `match` or `differs`: the manifest against the generated one, byte for byte. |
| `manifestDifferences` | list of strings | The top-level fields of the manifest that differ. |
| `archive` | string or `null` | For a zip: `match` when its bytes are the bytes that Vellum writes. `null` for a manifest alone. |
| `current` | boolean | `false` when an input moved after the export. |
| `moved` | list | Each input that moved: `kind`, `id`, `pinned`, `current`. |
| `expectedBundleHash` | string | The bundle hash that the pinned state generates. |
| `actualBundleHash` | string | The bundle hash of what you sent. |
| `files` | list | One entry for each file: `path`, `status`, `expectedSha256`, `actualSha256`. The `status` is `match`, `differs`, `missing` or `unknown-file`. |
| `selfCheck` | object | The self-check of the generated bundle. |
| `issued` | boolean | `true` when the pinned state has a seal that this instance issued for this project. Only such a state can be exported again. `false` does not make the bundle wrong. |

`matchesPinned: false` means that the bundle changed after the export. `current: false` means that the model moved after the export. The second is drift, not a changed bundle.

| Status | `type` | Cause |
|---|---|---|
| `400` | `invalid_bundle` | The body is not a manifest or a bundle that Vellum writes. Examples: an entry that the manifest does not list, an entry name two times, a path that is not plain, no pinned state. |
| `409` | `pinned_input_unavailable` | A pinned version is no longer stored. |
| `409` | `pinned_format_not_exported` | An export of a pinned state of the format `vellum-bundle/2`. |
| `409` | `pinned_not_issued` | An export of a pinned state with no seal that verifies. |
| `409` | `file_not_generated` | An export with `"format":"sdd"` when `SDD.md` was not generated. |
| `413` | `payload_too_large` | A manifest over the manifest limit. |
| `413` | `bundle_too_large` | An export over a bundle limit. |
