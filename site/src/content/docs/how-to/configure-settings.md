---
title: Configure a workspace
description: Change settings in the app, store credentials and read the audit log.
---

This guide shows you how to change the settings of a workspace. You need the owner role in the workspace to change them. Any member can read them.

Open the workspace on the home screen and select **Settings**.

## Defaults and workspace values

Each setting has an instance default. A workspace can set its own value.

- **Instance default** below a field means the workspace uses the default.
- **Set for this workspace** means the workspace has its own value. Select **Use the default** to remove it.

Select **Save changes** to apply your edits. Vellum applies them without a restart. If Vellum refuses a value, the field shows the reason and nothing is saved.

## Add an AI provider

Vellum works without an AI provider. Modelling, tracing, review rules and export need none.

1. Open **AI providers** and select a kind in **Add a provider**: OpenAI, Anthropic, Google Gemini, or OpenAI-compatible (for OpenRouter, Ollama and LM Studio).
2. Check the base URL and give the model name.
3. Turn **Structured output** or **Streaming** off if the endpoint does not support it.
4. Select **Save changes**.
5. Select **Set** next to **API key** and paste the key. A local model server usually needs no key.
6. Under **Task routing**, select the provider for each task. A task that is off does not use AI.

## Store a credential

Credentials are the AI provider keys, the GitHub token and the webhook signing secret.

Vellum encrypts a credential when you save it and never shows it again. The screen shows the date it changed and a fingerprint, which is the first 8 characters of its SHA-256 hash. Compare the fingerprint to tell two keys apart.

Select **Replace** to store a new value. Select **Remove** to delete it.

## URLs

Every URL must be an absolute `http` or `https` URL with no user name or password in it.

An instance can refuse URLs on private networks. The self-hosted profile allows them by default, so that a local model server works. The hosted profile refuses them by default. The instance administrator changes this under **Instance**.

## Private code and AI

Vellum always removes secrets from text before it goes to an AI provider.

**Privacy → Send private repository code to AI providers** is off by default. With it off, AI features see only the model and public sources. The instance administrator can prevent workspaces from turning it on.

## Read the audit log

**Audit log** lists changes to settings, credentials, keys and members: the time, the action, the target and the account. It never holds a value. Entries cannot be changed or deleted.
