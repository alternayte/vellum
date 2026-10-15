---
title: Configure a workspace
description: Change settings in the app, store credentials, set the start values and the bound of stress runs, and read the audit log.
---

This guide shows you how to change the settings of a workspace. You need the owner role in the workspace to change them. Any member can read them.

Open the workspace on the home screen and select **Settings**.

## Defaults and workspace values

Each setting has an instance default. A workspace can set its own value.

- **Instance default** below a field means the workspace uses the default.
- **Set for this workspace** means the workspace has its own value. Select **Use the default** to remove it.

Select **Save changes** to apply your edits. Vellum applies them without a restart. If Vellum refuses a value, the field shows the reason and nothing is saved.

Settings have a version. Vellum sends it with the settings, in the `version` field and in the `ETag` header. A save sends that version back in the `If-Match` header. If another person saved after you read the settings, Vellum answers `409` and saves nothing. Read the settings again, make your change and save. A save with no `If-Match` header gets `428`.

A text value must not contain control characters. A settings document can have 64 KB at most.

## Add an AI provider

Vellum works without an AI provider. Modelling, tracing, review rules and export need none.

1. Open **AI providers** and select a kind in **Add a provider**: OpenAI, Anthropic, Google Gemini, or OpenAI-compatible (for OpenRouter, Ollama and LM Studio).
2. Check the base URL and give the model name.
3. Turn **Structured output** or **Streaming** off if the endpoint does not support it.
4. Select **Save changes**.
5. Select **Set** next to **API key** and paste the key. A local model server usually needs no key. Save the provider before you set its key: Vellum accepts a key only for a provider that is in the saved settings.
6. Under **Task routing**, select the provider for each task. A task that is off does not use AI.

When you remove a provider and save, Vellum deletes the API key of that provider and records it in the audit log.

## Store a credential

Credentials are the AI provider keys, the tokens of GitHub, GitLab, Bitbucket and Azure DevOps, and the webhook signing secret.

Vellum encrypts a credential when you save it and never shows it again. The screen shows the date it changed, a fingerprint and the host that the credential is for.

### The fingerprint

The fingerprint has 8 characters. Vellum computes it from the value with a secret key that belongs to the workspace and to the master key. Use it to check that the stored value is the one you expect: save the same value again and the fingerprint stays the same.

The fingerprint is not a hash that you can compute yourself. The same value in two workspaces has two different fingerprints. The fingerprint changes when the instance administrator rotates the master key.

### A credential belongs to one host

Vellum binds each credential to the origin of the URL that it is for when you save it. The origin is the scheme, the host and the port, for example `https://api.github.com`.

- The GitHub token belongs to the origin of the GitHub API base URL.
- The GitLab, Bitbucket and Azure DevOps tokens each belong to the origin of the API base URL of their host. See [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/).
- An AI provider key belongs to the origin of the base URL of the provider. A provider with an empty base URL uses the address of its kind: `https://api.openai.com`, `https://api.anthropic.com` or `https://generativelanguage.googleapis.com`.
- The webhook signing secret belongs to the origin of the webhook URL. Set the webhook URL before you save the secret.

Vellum sends a credential to that origin only. If you change the URL to another origin, Vellum keeps the credential and does not send it to the new host. The screen then shows that the credential is for another host. Enter the value again to bind it to the new URL.

In the API, each entry of `GET /api/workspaces/{id}/secrets` has `boundOrigin` and `matchesCurrentUrl`. `matchesCurrentUrl` is `false` when the URL points at another origin, when the instance policy blocks the URL, or when Vellum cannot verify the stored row.

Select **Replace** to store a new value. Select **Remove** to delete it.

## URLs

Every URL must be an absolute `http` or `https` URL with no user name or password in it.

An instance can refuse URLs on private networks. The self-hosted profile allows them by default, so that a local model server works. The hosted profile refuses them by default. The instance administrator changes this under **Instance**.

In the hosted profile a URL must use `https`, because a credential goes to it. Plain `http` is accepted only for a host on a private network, and only when the instance allows private network URLs.

A host name has 253 characters at most, and each part between dots has 63 at most.

### When the instance policy changes

Vellum checks each stored URL against the instance policy each time it reads the settings. The instance administrator can make the policy stricter after you saved a URL. Vellum then stops using that URL at once:

- A blocked AI provider is disabled, and no task routes to it.
- A blocked git host base URL or webhook URL is not used.
- Vellum sends no credential to a blocked URL.

Your stored value does not change. The settings screen lists each blocked setting with the reason. In the API this list is `policyViolations`, with a `field` and a `message` for each entry. Change the URL, or ask the instance administrator to change the policy.

The same list shows an AI provider of a kind that the instance no longer allows, and the private code setting when the instance no longer allows it.

When the instance administrator saves the instance settings, Vellum reports how many workspaces now have a blocked setting.

## Set the start values of a stress profile

**Simulation** holds the values that the form of a new stress profile shows first.

1. Open **Simulation**.
2. Give the **Seed**.
3. Give the **Duration** in seconds of model time, from 1 to 3600.
4. Give the **Warm-up** in seconds. It must be less than the duration. Flows that start before this time are left out of the statistics.
5. Give the **Arrival rate** in flows per second.
6. Select **Save changes**.

A profile keeps its own values after you save it. A change here does not change a profile that exists. See [Stress a flow](/how-to/stress-a-flow/#create-a-profile).

## Bound the stress runs of the instance

You need to be the instance administrator for this task.

1. Open **Instance**.
2. Enter a whole number from 1 to 64 in **Stress runs at the same time**. The default is 4.
3. Select **Save** next to the field.

The number is for all workspaces together, in each app process. One run holds a processor thread until it ends. A change applies to the next run. A run over the number gets `429` with the message **The instance is busy with other stress runs**.

In the API the value is `policy.maxConcurrentStressRuns` of `PUT /api/instance/settings`. See [Settings](/reference/settings/#the-instance-policy).

## Private code and AI

Vellum always removes secrets from text before it goes to an AI provider.

**Privacy → Send private repository code to AI providers** is off by default. With it off, AI features see only the model and public sources. The instance administrator can prevent workspaces from turning it on.

## Read the audit log

**Audit log** lists changes to settings, credentials, keys, members and the price book: the time, the action, the target and the account. Entries cannot be changed or deleted.

An entry for a settings change names each field that changed. It holds no value, with one exception: for a URL field it holds the new origin, so that you can see where a credential can go.

An entry for a price names the price entry and holds its unit price. A price is not a secret.

An entry for a master key rotation shows only that a rotation took place. The instance administrator reads who did it, and with which key, in the instance audit log: **Settings → Instance → Instance audit log**, or `GET /api/instance/audit`.
