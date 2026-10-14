---
title: AI provider and GitHub connection problems
description: Each result of a connection test and of a failed scan, its cause and the fix.
---

This page lists the results of **Test connection** for an AI provider and for GitHub, the reasons why a scan stops and the texts that the assistant shows when it cannot answer.

The codes on this page are the `code` of the test result or the `type` of the error. Use them to find an entry.

## An address is refused by policy

**Symptom:** One of these texts.

- AI: `The instance policy refuses the provider's base URL. It must be an address that the policy allows: a public address, and https where the instance requires it.` Code `url_refused`.
- GitHub: `The instance policy refuses the GitHub API base URL. It must be a public http or https address.` Code `url_refused`.
- When you save a setting, one of these field messages:

| Message | Cause |
|---|---|
| `Must be an absolute http or https URL.` | The value is not a URL, or it has another scheme. |
| `Must not contain credentials.` | The URL has a user name or a password in it. |
| `Must not point at this machine or a private network.` | The host is `localhost` or has a private address, and the instance does not allow private network URLs. |
| `Must use https. Plain http is allowed only for an address on this machine or a private network, and only when the instance policy allows private network URLs.` | The instance requires `https`. The hosted profile does. |
| `The host name does not resolve.` | DNS has no address for the host. |

**Cause:** Vellum checks each outbound address. In the hosted profile, addresses on private networks are refused. Vellum connects only to the address that it checked.

**Fix:**

1. Correct the base URL.
2. For a model server on your own network, ask the instance administrator to allow private network URLs in the instance settings. See [Configure settings](/how-to/configure-settings/).
3. Select **Test connection** again.

## The base URL redirects

**Symptom:** AI: `The provider's base URL redirects to another address. Set the base URL to the final address.` GitHub: `The GitHub API base URL redirects to another address. Set github.apiBaseUrl to the final address.` Code `redirect_refused`.

**Cause:** Vellum does not follow a redirect to another host.

**Fix:**

1. Set the base URL to the final address.
2. Test again.

## The credential was saved for another host

**Symptom:** AI: `The API key was saved for <host>. Enter it again to use it with <new host>.` Code `key_saved_for_another_host`. GitHub: `The token was saved for another host. Enter it again to use it with the current github.apiBaseUrl.` Code `token_saved_for_another_host`.

**Cause:** Vellum binds each credential to the host of the base URL at the time you saved it. After you change the base URL, Vellum does not send the old credential to the new host.

**Fix:**

1. Enter the key or the token again in the settings.
2. Save.
3. Test again.

## The credential cannot be decrypted

**Symptom:** `The stored API key cannot be decrypted with the loaded master key. Save the key again.` or `The stored token cannot be decrypted with the loaded master key. Save the token again.` Code `secret_unavailable`.

**Cause:** The row was encrypted with a master key that is not loaded.

**Fix:**

1. Enter the key or the token again and save.
2. If many credentials fail, the master key is wrong. See [Lost master key](/runbooks/lost-master-key/).

## A stored value cannot be sent

**Symptom:** A text that starts with `The stored API key, the model name or the base URL holds a line break` or `The stored token or the GitHub API base URL holds a line break`. Code `invalid_setting`.

**Cause:** A value has a control character, or the credential has a character outside ASCII. A frequent cause is a line break that was copied with the key. Nothing was sent.

**Fix:**

1. Enter the credential again with no line break.
2. Check the model name and the base URL.

## AI provider: results of Test connection

Vellum makes one small real call. These are the results.

| Code | Text | Fix |
|---|---|---|
| none | The model answered. | No fix is necessary. |
| `not_configured` | `The provider needs a model name, a base URL where the kind has none of its own, and an API key in the workspace secrets.` | Complete the provider entry and save the key. |
| `disabled_by_policy` | `The instance policy does not allow the provider kind '<kind>'.` | Ask the instance administrator, or use another kind. |
| `invalid_api_key` | `The provider refused the API key. Save a valid key for this provider in the workspace secrets.` | Save a valid key. |
| `permission_denied` | `The provider does not allow this API key to use this model or endpoint.` | Check that the key is active and that its account can use the model. |
| `model_not_found` | `The provider has no such model at this base URL. Check the model name and the base URL.` | Correct the model name or the base URL. |
| `rate_limited` | `The provider's rate limit is reached. Try again later.` | Wait, then test again. |
| `rate_limited` | `The provider's requests-per-minute limit in settings is reached. Try again in a minute.` | Wait one minute. This limit is in the Vellum settings of the provider. |
| `quota_exceeded` | `The provider account has no quota or credit left. Check the billing of the provider account.` | Add credit at the provider. |
| `budget_exceeded` | `The monthly AI budget is used up.` | See the entry below. |
| `invalid_request` | `The provider rejected the request. A provider setting (model, structured output, or the optional temperature) or the schema does not fit this model.` | Check the model, the structured output setting and the temperature. |
| `unsupported_parameter` | `The model does not accept a setting of this provider entry.` | Clear the temperature of the provider entry. |
| `provider_unavailable` | `The provider reported a server error or is overloaded. Try again later.` | Wait, then test again. |
| `timeout` | `The provider did not answer within the configured timeout.` | Test again. For a slow local model, increase the **Timeout** of the provider entry. |
| `connection_failed` | `No connection to the provider's base URL could be made. Check the base URL.` | Check the base URL and that the server runs. |
| `connection_lost` | `The connection to the provider broke before the answer arrived.` | Test again. |
| `malformed_response` | `The answer was not in the provider's documented format. Check the base URL and the provider kind.` | Check that the provider kind fits the server. |
| `response_too_large` | `The provider's answer is larger than Vellum accepts.` | Use another model or a smaller task. |

See [Connect an AI provider](/how-to/connect-an-ai-provider/).

## The model does not support structured output

**Symptom:** The test succeeds and shows one of these notes.

- `The provider rejected a request in its structured-output mode. Vellum then sends the schema in the prompt and checks the answer itself (usage note 'schema_rejected_fallback'), which costs one rejected call for each request.`
- `The provider accepted its structured-output mode and gave an answer outside the schema.`

**Cause:** The provider entry says that the model supports structured output, and the model does not.

**Fix:**

1. Open the provider entry in the settings.
2. Switch off the structured output option.
3. Save and test again. Vellum then sends the schema in the prompt and checks the answer itself.

## The AI budget is reached

**Symptom:** `The monthly AI budget is used up.` or `A budget is set and the model has no price, so the budget cannot be kept. Add a price for the model or remove the budget.` Code `budget_exceeded`.

**Cause:** The workspace has a monthly budget. Vellum refuses a call that can exceed it. Vellum also refuses when it cannot compute the cost because the model has no price.

**Fix:**

1. For the first text, increase the budget in the workspace settings or wait for the next month.
2. For the second text, add a price for the model or remove the budget. See [AI usage and prices](/reference/ai-usage-and-prices/).

## The assistant cannot answer

The assistant panel shows one of three headings, then the reason.

| Heading | Meaning | Fix |
|---|---|---|
| **No AI provider is set for this task.** | No provider is routed to the task, or AI is off. | Add a provider and route the task: [Connect an AI provider](/how-to/connect-an-ai-provider/). |
| **The assistant cannot answer now.** | The provider, the budget or a limit refused the call. The reason below the heading is one of the texts on this page. | Find the reason on this page. When the panel shows **Try again in <n> seconds.**, wait for that time. |
| **The assistant did not give a usable answer.** | The model answered, and the answer did not fit the schema after two corrections, or it was cut at the output limit. | Try again. Use a model with a larger output limit or with better structured output. |

Vellum does not send private repository code to a provider while the setting `privacy.sendPrivateCodeToAi` is off. The rest of Vellum works without AI.

## GitHub: results of Test connection

A successful test names the account of the token and the requests that remain. Without a token it says that only public repositories are reachable.

| Code | Text | Fix |
|---|---|---|
| `unauthorized` | `GitHub refused the token. Save a new token in the workspace secrets.` | The token is expired, revoked or mistyped. Save a new one. |
| `forbidden` | `The token is valid, and GitHub does not allow it to make this call. Check the token's permissions.` | Give the token read access to the repository contents. |
| `not_found` | `The GitHub API base URL has no REST API at that address. Check github.apiBaseUrl.` | Correct the **API base URL**. For GitHub Enterprise Server an example is `https://github.example.com/api/v3`. |
| `rate_limited` | `GitHub's rate limit is used up. It resets at <time> UTC.` | Wait for the reset. Add a token: a token has a higher limit than anonymous access. |
| `timeout` | `GitHub did not answer in time.` | Test again. |
| `network` | `No connection to the GitHub API base URL could be made.` | Check the address and the network of the container. |
| `server_error` | `GitHub reported a server error. Try again later.` | Wait, then test again. |
| `unexpected_response` | `The answer was not a GitHub API answer. Check github.apiBaseUrl.` | Correct the base URL. A proxy page or a sign-in page gives this result. |

The codes `url_refused`, `invalid_setting`, `token_saved_for_another_host`, `secret_unavailable` and `redirect_refused` are in the entries at the top of this page.

See [Connect GitHub](/how-to/connect-github/).

## Repository not found, or private

**Symptom:** A scan fails with the code `not_found` and one of these texts.

- Without a token: `GitHub has no public repository with this name. Check the name. If the repository is private, save a token in the workspace secrets (github:token). Then resume the scan.`
- With a token: `GitHub has no repository with this name that the workspace's token can see. Check the name, and check that the token has access to the repository. Then resume the scan.`

**Cause:** GitHub gives the same answer for a repository that does not exist and for a private repository that the caller cannot see. Vellum cannot tell the two cases apart.

**Fix:**

1. Check the owner and the name.
2. For a private repository, save a token that has read access to its contents.
3. Select **Resume** on the scan.

A scan with the code `forbidden` means that the token is valid and has no read access to this repository. A scan with the code `ref_not_found` means that the repository has no branch, tag or commit with the name that you gave. Start a new scan with another ref.

## A scan is paused

**Symptom:** The scan list shows **Paused by the GitHub rate limit** or **Paused by the GitHub rate limit until <time>**. The scan has the status `waiting`.

**Cause:** One of two.

- GitHub's rate limit is used up. The scan waits for the reset.
- An unexpected error stopped the scan and Vellum will try again. The app shows the same text for this case. `GET /api/projects/{projectId}/imports/{importId}` gives the real reason in `error`: the code `internal_error` with `It starts again by itself at the time in retryAt.`

**Fix:**

1. Wait. A waiting scan starts again by itself at the time in `retryAt`. What it read is kept.
2. To get a higher limit, save a token in the workspace settings.
3. To stop the wait, select **Cancel**.

You cannot resume a waiting scan. Resume applies to a failed or a cancelled scan.

## A scan failed

**Symptom:** The scan list shows the reason in the row of the scan.

| Code | Meaning | Fix |
|---|---|---|
| `rate_limited` | `GitHub's rate limit stayed used up through many retries.` | Select **Resume** later. Save a token for a higher limit. |
| `network` | GitHub did not answer in time, or reported a server error. What was read is kept. | Select **Resume**. |
| `unauthorized`, `forbidden`, `url_refused`, `secret_unavailable`, `token_saved_for_another_host`, `invalid_setting`, `redirect_refused` | The same causes as in the connection test. | Correct the setting, then select **Resume**. |
| `commit_not_found` | `GitHub no longer has the commit this scan is pinned to.` | Start a new scan. |
| `tree_truncated`, `too_large` | The file list of the repository is larger than Vellum reads in one scan. | Vellum cannot scan a repository of this size. |
| `stored_content_missing` | The stored content of a file of this scan is missing. | Select **Resume**. Vellum fetches the file again at the same commit. |
| `model_busy` | The model changed each time the scan compared its result with it. Nothing was written. | Select **Resume** when nobody edits the model. |
| `worker_crashed` | The worker that ran the scan stopped two times in a row. | Select **Resume**. Report the scan ID to the operator. |
| `internal_error` | An unexpected error stopped the scan on each automatic attempt. | Select **Resume**. Report the scan ID to the operator. |
| `project_not_found` | The project of the scan no longer exists. | No fix. |

**Resume** calls `POST /api/projects/{projectId}/imports/{importId}/resume`. A resumed scan reads the same commit and keeps what it read.

| Status | Type | Cause |
|---|---|---|
| `409` | `scan_not_resumable` | The scan is not failed or cancelled, or it failed in a way that a resume meets again: `tree_truncated`, `too_large`, `project_not_found`, `ref_not_found`, `commit_not_found`, `pull_request_not_found` or `pull_request_head_unreadable`. Start a new scan. The scan has `canResume: false`, and the app shows no **Resume** on it. |
| `409` | `scan_expired` | The scan stopped too long ago and its files are no longer kept. Start a new scan. |
| `409` | `resume_limit_reached` | The scan was resumed the maximum number of times and stopped each time. Start a new scan. |
| `409` | `scan_in_progress` | Another scan of this repository is queued or running for the project. |

## A scan is partial

**Symptom:** The scan shows **Partial scan: a limit stopped it before every file was read**.

**Cause:** The scan reached **Files per import**, **Largest file** or **Total size**.

**Fix:**

1. Scan fewer folders with **Only these folders**. As an alternative, a workspace owner increases the limits under **Settings → Import limits**.
2. Start a new scan. A scan uses the limits that were set when it started.

A partial scan proposes no removals. See [Import a repository](/how-to/import-a-repository/#limits).

## A scan is refused before it starts

| Status | Type | Cause | Fix |
|---|---|---|---|
| `409` | `github_not_configured` | The workspace has no usable GitHub API base URL. | See [Connect GitHub](/how-to/connect-github/). |
| `409` | `scan_in_progress` | A scan of this repository is queued or running for the project. | Wait for it, or cancel it. |
| `429` | `scan_limit_reached` | The workspace reached **Repository scans per day**. The day is a UTC day. | Wait for the next day, or increase the limit under **Settings → Usage limits**. |

## See also

- [What leaves your instance](/concepts/what-leaves-your-instance/)
- [Extraction rules](/reference/extraction-rules/)
