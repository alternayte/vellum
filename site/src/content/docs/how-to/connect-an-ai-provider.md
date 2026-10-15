---
title: Connect an AI provider
description: Add a provider, store its key, test the connection and route tasks to it.
---

This guide shows you how to connect an AI provider to a workspace. You need the owner role in the workspace.

Vellum works without a provider. Modelling, tracing, review rules, imports and export need none.

The OpenAI-compatible kind is tested against a live Ollama server. The OpenAI, Anthropic and Gemini kinds are tested against sample responses of each API.

## Add the provider

1. Open the workspace on the home screen and select **Settings**, then **AI providers**.
2. Select a kind in **Add a provider**: OpenAI, Anthropic, Google Gemini, or OpenAI-compatible.
3. Give the model name that the provider expects. Vellum has no default model.
4. Check the base URL. Leave the vendor URL as it is, unless you use a gateway.
5. Select **Save changes**.

Use **OpenAI-compatible** for OpenRouter, Ollama and LM Studio. For a local model server, give its URL, for example `http://localhost:11434/v1`.

## Store the key

1. Select **Set** next to **API key** on the saved provider.
2. Paste the key and select **Save**.

Vellum encrypts the key and does not show it again. The key is bound to the host of the base URL. If you change the base URL to another host, Vellum does not send the key there. The row then asks you to enter the key again.

A key holds printable ASCII characters only.

## Test the connection

Select **Test connection**. Vellum makes one small real call with the saved settings and reports the result.

| Result | What to do |
|---|---|
| The model answered | Nothing. The line shows the latency and the model the provider reports. |
| It does not return structured output | Turn **Structured output** off for this provider. Vellum then validates the reply itself. |
| Invalid key | Enter the key again. |
| Model not found | Correct the model name. |
| No connection could be made | Check the base URL and that the server runs. |
| The URL is refused by policy | The instance refuses this address. Ask the instance administrator. |

A test call is metered like any other call. Its purpose is `test-connection`.

## Route tasks

Under **Task routing**, select the provider for each task: infer architecture, propose a flow, review and explain. A task that is off does not use AI.

## Set limits

- **Timeout** stops a call that takes too long.
- **Requests per minute** limits the call rate.
- **Budget per month** stops calls when the estimated cost of the month reaches the limit.

A budget needs a price for the model. Without a price Vellum cannot keep the budget, so it refuses the call. The instance administrator enters prices under **Settings → Instance → Model prices**.

**Temperature** is optional. Leave it empty unless the model accepts it. Some models refuse the parameter; the test then says so.
