---
title: Settings
description: Every key of the workspace settings document and the instance settings document, with its type, default, limits and effect.
---

Vellum has two settings documents. Both are JSON.

- The instance settings hold a policy and a set of defaults. An instance administrator changes them.
- The workspace settings hold the values that one workspace sets over the defaults. A workspace owner changes them.

[Configure a workspace](/how-to/configure-settings/) gives the steps. [Bootstrap environment](/reference/bootstrap-environment/) lists the values that Vellum reads at start-up and that are not in these documents.

## Levels

| Level | Meaning |
|---|---|
| Instance default | The value that applies to a workspace that sets no value of its own. It is in `defaults` of the instance settings. |
| Workspace override | The value that one workspace sets. It is in `overrides` of the workspace settings. |
| Instance-only policy | A value in `policy` of the instance settings. A workspace cannot set it. |

Every key of the settings document can be an instance default and a workspace override. The five policy keys are instance-only.

The effective document of a workspace is the instance defaults with the workspace overrides laid over them. The merge rules are:

- An object merges field by field.
- A list replaces the list under it as a whole. A workspace that sets `ai.providers` replaces the full default list.
- A value replaces the value under it.
- An explicit `null` replaces the value under it. A workspace uses `null` to clear a default that can be empty.

An instance administrator can leave a key out of `defaults`. That key then takes the built-in default in the tables below.

## The settings document

The column **Default** is the built-in default of a new instance. The column **UI** gives the section of the **Workspace settings** screen and the label of the field.

### `ai`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `ai.providers` | list of provider objects | `[]` | 20 entries at most | **AI providers** | The AI providers that the workspace can use. |
| `ai.taskRouting.inferArchitecture` | GUID or `null` | `null` | `null` or the `id` of an enabled provider | **AI providers** → **Task routing** → **Infer architecture** | The provider for the task that groups imported facts into services. `null` turns the task off. |
| `ai.taskRouting.proposeFlow` | GUID or `null` | `null` | `null` or the `id` of an enabled provider | **Task routing** → **Propose a flow** | The provider for the task that turns a request in words into a proposal. |
| `ai.taskRouting.critique` | GUID or `null` | `null` | `null` or the `id` of an enabled provider | **Task routing** → **Review** | The provider for the task that asks questions about the design. |
| `ai.taskRouting.explain` | GUID or `null` | `null` | `null` or the `id` of an enabled provider | **Task routing** → **Explain** | The provider for the task that explains a part of the model. |

In the UI the empty routing option is **Off: this task does not use AI**.

Each entry of `ai.providers` has these fields. `id`, `kind`, `name` and `model` are required. The other fields take the default when you leave them out.

| Field | Type | Default | Allowed | UI label | Effect |
|---|---|---|---|---|---|
| `id` | GUID | none (required) | Not the empty GUID. Unique in the list. | none | The identity of the provider. Task routing and the API key name refer to it. |
| `kind` | string | none (required) | `openai`, `anthropic`, `gemini`, `openai-compatible`. The kind must also be in `policy.allowedAiProviderKinds`. | **Add a provider** | The wire protocol. |
| `name` | string | none (required) | 1 to 100 characters | **Provider name** | The name on the screen. |
| `model` | string | none (required) | 200 characters at most. An enabled provider needs a model name that is not empty. | **Model** | The model name that Vellum sends. |
| `baseUrl` | string | `""` | Empty, or a URL that obeys the [URL rules](#url-rules). `openai-compatible` needs a URL. | **Base URL** | The address of the provider. Empty uses the address of the kind: `https://api.openai.com/v1`, `https://api.anthropic.com` or `https://generativelanguage.googleapis.com`. |
| `enabled` | boolean | `true` | | **Enable** switch | A provider that is off serves no task. |
| `supportsStructuredOutput` | boolean | `true` | | **Structured output** | Tells Vellum if the endpoint accepts a JSON schema for its answer. |
| `supportsStreaming` | boolean | `true` | | **Streaming** | Tells Vellum if the endpoint can stream its answer. |
| `temperature` | number or `null` | `null` | `null`, or 0 to 2 | **Temperature** | The sampling temperature. `null` sends none. |
| `timeoutSeconds` | integer | `60` | 1 to 600 | **Timeout (seconds)** | The time limit of one request to the provider. |
| `monthlyBudgetUsd` | number or `null` | `null` | `null`, or 0 to 1000000 | **Budget per month (USD)** | The spend limit of this provider in one calendar month. `null` sets no limit. See [AI usage and prices](/reference/ai-usage-and-prices/). |
| `requestsPerMinute` | integer or `null` | `null` | `null`, or 1 to 100000 | **Requests per minute** | The request rate limit of this provider. `null` sets no limit. |

### `github`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `github.apiBaseUrl` | string | `https://api.github.com` | A URL that obeys the [URL rules](#url-rules) | **GitHub** → **API base URL** | The address of the GitHub API. Change it only for GitHub Enterprise Server. |
| `github.defaultRef` | string | `""` | 255 characters at most. No white space. No control characters. | **GitHub** → **Default ref** | The branch or tag that an import reads when the request names none. Empty uses the default branch of the repository. |

### `import`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `import.maxFiles` | integer | `2000` | 1 to 100000 | **Import limits** → **Files per import** | The largest number of files that one scan reads. |
| `import.maxFileBytes` | integer | `262144` | 1 to 16777216 | **Import limits** → **Largest file** | The largest file, in bytes, that a scan reads. |
| `import.maxTotalBytes` | integer | `20971520` | 1 to 1073741824. Not less than `import.maxFileBytes`. | **Import limits** → **Total size** | The largest total size, in bytes, that one scan reads. |
| `import.excludeGlobs` | list of strings | `**/node_modules/**`, `**/vendor/**`, `**/bin/**`, `**/obj/**`, `**/*.min.*` | 100 patterns at most. Each pattern has 1 to 200 characters. | **Import limits** → **Excluded paths** | Paths that a scan does not read. |

A scan records the limits that applied when it was requested.

### `cost`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `cost.provider` | string | `""` | 100 characters at most | **Cost** → **Cloud provider** | Stored and validated only. No backend code reads it. |
| `cost.region` | string | `""` | 100 characters at most | **Cost** → **Region** | Stored and validated only. No backend code reads it. |
| `cost.currency` | string | `USD` | Three upper-case letters `A` to `Z` | **Cost** → **Currency** | The currency of an estimate. An estimate flags a line that has a price in another currency. Vellum converts no currency. The Azure price search uses it when the request gives no currency. |
| `cost.azureRetailPricesBaseUrl` | string | `https://prices.azure.com` | Empty, or a URL that obeys the [URL rules](#url-rules) | **Cost** → **Azure price API** | The address of the Azure Retail Prices API or of a mirror. Empty turns the price search off. Vellum sends no credential to it. |
| `cost.stalePriceAfterDays` | integer | `90` | 1 to 3650 | **Cost** → **A price is stale after** | An estimate flags a price that was entered or fetched more than this number of days before the date of the estimate. |

The AWS and Google Cloud price APIs have fixed addresses and no setting. The Google Cloud price search needs the workspace secret `gcp:apiKey`; Vellum sends it to `https://cloudbilling.googleapis.com` only. See [Estimate costs](/how-to/estimate-costs/#aws-and-google-cloud).

A save does not resolve the host name of `cost.azureRetailPricesBaseUrl`. The price search resolves it and checks the addresses when it runs. See [Cost model](/reference/cost-model/).

### `simulation`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `simulation.seed` | integer | `1` | 0 to 2147483647 | **Simulation** → **Seed** | The seed that a new stress profile starts with. |
| `simulation.durationSeconds` | integer | `300` | 1 to 3600 | **Simulation** → **Duration** | The duration that a new stress profile starts with, in seconds of model time. |
| `simulation.warmUpSeconds` | integer | `5` | 0 or more, and less than `simulation.durationSeconds` | **Simulation** → **Warm-up** | The warm-up that a new stress profile starts with. Flows that start before this time are left out of the statistics. |
| `simulation.arrivalRatePerSecond` | number | `10` | More than 0, and 100000 at most | **Simulation** → **Arrival rate** | The arrival rate that a new stress profile starts with, in flows per second. |

These four values are what the form of a new stress profile shows first. `GET /api/projects/{projectId}/stress/defaults` returns them for the workspace of the project. A profile keeps its own values after it is saved. A change of these settings does not change a profile that exists, and a run never reads them. See [Stress model](/reference/stress-model/#defaults-of-a-new-profile).

### `privacy`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `privacy.sendPrivateCodeToAi` | boolean | `false` | `true` only when `policy.allowSendingPrivateCodeToAi` is `true` | **Privacy** → **Send private repository code to AI providers** | With `false`, Vellum refuses an AI request that holds text from a private repository. See [What leaves your instance](/concepts/what-leaves-your-instance/). |

### `usage`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `usage.monthlyAiBudgetUsd` | number or `null` | `null` | `null`, or 0 to 1000000 | **Usage limits** → **AI budget per month** | The AI spend limit of the workspace in one calendar month, over all providers. `null` sets no limit. |
| `usage.maxScansPerDay` | integer | `20` | 0 to 10000 | **Usage limits** → **Repository scans per day** | The number of repository scans that the workspace can start in one day. |

### `notifications`

| JSON path | Type | Default | Allowed | UI | Effect |
|---|---|---|---|---|---|
| `notifications.webhookUrl` | string or `null` | `null` | `null`, empty, or a URL that obeys the [URL rules](#url-rules) | **Notifications** → **Webhook URL** | Vellum sends the comment events `comment.created`, `comment.mentioned` and `comment.resolved` to it. The secret `notifications:webhookSecret` is bound to its origin and signs each request. See [Comment on a design](/how-to/comment-on-a-design/#send-comment-events-to-a-webhook). |

## The instance policy

The policy is in `policy` of the instance settings. A `PUT` must state the first four keys. `policy.maxConcurrentStressRuns` is optional: a `PUT` that leaves it out sets it to `4`. The policy holds no other field.

| JSON path | Type | Default, self-hosted profile | Default, hosted profile | UI | Effect |
|---|---|---|---|---|---|
| `policy.allowSelfRegistration` | boolean | `false` | `true` | **Instance** → **Let people register themselves** | With `false`, registration is closed after first-run setup. |
| `policy.allowPrivateNetworkUrls` | boolean | `true` | `false` | **Instance** → **Allow URLs on private networks** | With `false`, Vellum refuses a settings URL that points at a private, loopback or link-local address. |
| `policy.allowedAiProviderKinds` | list of strings | all four kinds | all four kinds | none (API only) | The provider kinds that a workspace can use. No repeats. Each entry is `openai`, `anthropic`, `gemini` or `openai-compatible`. |
| `policy.allowSendingPrivateCodeToAi` | boolean | `true` | `false` | **Instance** → **Let workspaces send private code to AI** | With `false`, no workspace can set `privacy.sendPrivateCodeToAi` to `true`. |
| `policy.maxConcurrentStressRuns` | integer, 1 to 64 | `4` | `4` | **Instance** → **Stress runs at the same time** | The number of stress runs that one app process does at the same time, for all workspaces together. A run over the number gets `429` with the type `busy`. |

One stress run holds a processor thread and memory until it ends. So `policy.maxConcurrentStressRuns` bounds what stress runs take from the instance. Vellum reads the value for each run. A change applies to the next run with no restart. Runs that are in progress when the value goes down continue to their end. A value outside 1 to 64 is refused with `400` on the field `policy.maxConcurrentStressRuns`. See [Stress model](/reference/stress-model/#runs-at-the-same-time).

Vellum checks each stored workspace value against the policy each time it reads the settings. A value that the policy refuses does not apply, and the stored value does not change. `GET /api/workspaces/{workspaceId}/settings` lists such values in `policyViolations`, with a `field` and a `message` for each.

## URL rules

These rules apply to `ai.providers[].baseUrl`, `github.apiBaseUrl`, `cost.azureRetailPricesBaseUrl` and `notifications.webhookUrl`.

- The URL has 2000 characters at most.
- The URL is absolute, with the scheme `http` or `https`, and has no user name or password.
- When `policy.allowPrivateNetworkUrls` is `false`, the host must not resolve to a private network address.
- In the hosted profile the URL must use `https`. Plain `http` is accepted only for a host on a private network, and only when the policy allows private network URLs.

[Security model](/concepts/security-model/#outbound-requests) explains the reason.

## Endpoints

| Endpoint | Who | Purpose |
|---|---|---|
| `GET /api/workspaces/{workspaceId}/settings` | Any member | Returns `effective`, `overrides`, `defaults`, `policyViolations` and `version`. |
| `PUT /api/workspaces/{workspaceId}/settings` | Owner, signed in as a person | Replaces the overrides. The body is the full overrides document. |
| `GET /api/workspaces/{workspaceId}/secrets` | Owner | Lists the status of each credential. It never returns a value. |
| `PUT /api/workspaces/{workspaceId}/secrets/{name}` | Owner, signed in as a person | Stores a credential. The body is `{"value": "<text>"}`. Answers `204`. |
| `DELETE /api/workspaces/{workspaceId}/secrets/{name}` | Owner, signed in as a person | Deletes a credential. Answers `204`, also when the credential is not set. |
| `GET /api/workspaces/{workspaceId}/audit` | Owner | Lists the audit entries of the workspace. See [Configure a workspace](/how-to/configure-settings/#read-the-audit-log). |
| `GET /api/instance` | Any signed-in account | Returns `profile`, `version`, `isInstanceAdmin` and `allowSelfRegistration`. |
| `GET /api/instance/settings` | Instance administrator | Returns `policy`, `defaults`, `version` and `workspacesWithPolicyViolations`. |
| `PUT /api/instance/settings` | Instance administrator, signed in as a person | Replaces the policy and the defaults. The body has exactly `policy` and `defaults`. |
| `GET /api/instance/audit` | Instance administrator | Lists the audit entries of the instance. |

An agent token cannot call an endpoint that is marked "signed in as a person".

The body of `PUT /api/workspaces/{workspaceId}/settings` is a partial settings document. It replaces the stored overrides as a whole: a key that you leave out goes back to the instance default. An unknown field, a field that repeats, a `null` where no `null` is allowed and a value of the wrong type are refused with `400`.

When a save removes a provider from `ai.providers`, Vellum deletes the API key of that provider.

## Concurrency

| Item | Behaviour |
|---|---|
| `version` | A whole number in the response body. It goes up by 1 on each save. The workspace settings and the instance settings each have their own. |
| `ETag` | The response header that carries the version in quotes, for example `"3"`. |
| `If-Match` | The request header of a `PUT`. It carries the version that you read, in quotes. |
| `428` | The `PUT` has no `If-Match` header. Code `precondition_required`. |
| `400` | `If-Match` is not a version in quotes. Code `validation_error`, field `If-Match`. |
| `409` | The stored version is not the version in `If-Match`. Code `conflict`. Nothing is saved. Read the settings again, make the change and save. |
| `409` | The instance settings changed three times in a row while a workspace save was checked. Nothing is saved. Send the request again. |

```sh
curl -s -X PUT "http://localhost:8080/api/workspaces/<workspace-id>/settings" \
  -b cookies.txt \
  -H 'Content-Type: application/json' \
  -H 'If-Match: "3"' \
  -d '{"usage":{"maxScansPerDay":5}}'
```

## Size limits

| Item | Limit |
|---|---|
| Body of `PUT /api/workspaces/{workspaceId}/settings` | 65536 bytes |
| Body of `PUT /api/instance/settings` | 69632 bytes |
| Body of `PUT /api/workspaces/{workspaceId}/secrets/{name}` | 32768 bytes |
| Secret value | 1 to 4096 characters |
| Text value in a settings document | No control characters, in a value or in a field name |

A body over the limit gets `413`.

## Secret names

`PUT /api/workspaces/{workspaceId}/secrets/{name}` accepts these names only. Any other name gets `400`.

| Name | Credential | Bound to the origin of | Value rule |
|---|---|---|---|
| `github:token` | The GitHub access token | `github.apiBaseUrl` | Printable ASCII only |
| `ai:<provider id>:apiKey` | The API key of one AI provider. `<provider id>` is the `id` of the provider, in the form `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`. | The `baseUrl` of the provider, or the address of its kind | Printable ASCII only |
| `notifications:webhookSecret` | The webhook signing secret | `notifications.webhookUrl` | No control characters |
| `gcp:apiKey` | The API key of the Google Cloud Billing Catalog API | `https://cloudbilling.googleapis.com`, a fixed address | Printable ASCII only |

Vellum refuses a secret with `400` in these cases:

- The name is `ai:<provider id>:apiKey` and no provider with that `id` is in the effective settings. Save the provider first.
- The name is `notifications:webhookSecret` and `notifications.webhookUrl` is not set.
- The instance policy blocks the URL that the secret is for.

Each entry of `GET /api/workspaces/{workspaceId}/secrets` has these fields.

| Field | Type | Meaning |
|---|---|---|
| `name` | string | The secret name. |
| `isSet` | boolean | `true` when a value is stored. |
| `updatedAt` | timestamp or `null` | When the value was stored. |
| `updatedBy` | string or `null` | The account that stored it. |
| `fingerprint` | string or `null` | The fingerprint of the value. `null` when Vellum cannot verify the stored row with the master key. |
| `boundOrigin` | string or `null` | The origin that the value was saved for. `null` when Vellum cannot verify the stored row. |
| `matchesCurrentUrl` | boolean | `false` when the settings now point at another origin, when the policy blocks the URL, or when Vellum cannot verify the stored row. Vellum then does not send the credential. |

[Security model](/concepts/security-model/#secrets) explains the encryption, the fingerprint and the origin binding.
