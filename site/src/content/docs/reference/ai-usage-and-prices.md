---
title: AI usage and prices
description: What Vellum records for each AI call, how it estimates cost and how budgets work.
---

This page describes the usage record, the price table and the budget rules.

## What a usage record holds

Vellum writes one record for each call to a provider. A request that Vellum sends again for a corrected answer is one more call and one more record. A call that Vellum refuses before it starts, for a budget or a rate limit, has no record.

| Field | Content |
|---|---|
| Provider and model | The configured provider, its kind, the model name and the model that the provider reports. |
| Task and purpose | The task that made the call: `inferArchitecture`, `proposeFlow`, `critique`, `explain` or `test`. The purpose is a fixed label: `infer-architecture`, `propose-flow`, `critique`, `explain` or `test-connection`. |
| Prompt template version | The version of the prompt template that built the request. |
| Tokens | The input and output counts the provider reports. Empty when it reports none, or when the count is below 0 or above 100,000,000. |
| Estimated cost | In USD. Empty when the model has no price. |
| Outcome | See the table below. |
| Usage note | Empty, or why the cost and the counts are not the counts of the provider. See the table below. |
| Error code | The code of a provider error, when the call has one. |
| Duration, time, account | When the call started, how long it took and who started it. |

A record never holds the prompt or the reply.

| Outcome | Meaning |
|---|---|
| `started` | The call runs. |
| `succeeded` | The provider gave a complete answer. A structured answer matched its schema. |
| `invalid_output` | The answer did not match its schema. |
| `truncated` | The output limit cut the answer. |
| `refused` | The provider refused to answer. |
| `provider_error` | The provider returned an error. |
| `rate_limited` | The provider returned a rate limit error. |
| `timed_out` | The provider did not answer in the timeout of the provider entry. |
| `cancelled` | The caller cancelled the call or stopped reading the stream. |
| `failed` | An error occurred inside Vellum. |
| `abandoned` | The process stopped during the call. Vellum marks a record that is `started` for more than 2 hours as `abandoned` at the next call of the workspace. |

| Usage note | Meaning |
|---|---|
| `cost_estimated` | The provider reported no count for the input or the output. The cost is an estimate. |
| `usage_out_of_range` | A reported count was not a possible count. Vellum dropped it and estimated the cost. |
| `reaped` | The record was abandoned. |
| `schema_rejected_fallback` | The provider rejected its structured-output mode. This call sent the schema in the prompt. |

## Read the usage

Open **Settings → Usage limits**. The table **AI use this month** shows the calls of the current month for each provider, model and task.

- **unknown** means no call of the row has a known cost. The model has no price.
- **at least** means some calls of the row have no known cost.
- **up to** means the cost of some call is an estimate, because the provider reported no usable token count. Vellum then counts the most the call could cost.
- **not reported** in a token column means no call of the row reported that count.

`GET /api/workspaces/{workspaceId}/ai/usage` returns the same totals. A member of the workspace with the Viewer role or higher can call it. An agent token cannot.

| Query parameter | Meaning |
|---|---|
| `from` | The start of the range. Default: the start of the current calendar month in UTC. |
| `to` | The end of the range. Default: now. |

`from` must be earlier than `to`. The range is at most 366 days. Each item of `totals` has `providerId`, `providerKind`, `model`, `task`, `calls`, `inputTokens`, `outputTokens`, `costUsd`, `costKnown` and `costEstimated`.

## Prices

Vellum ships no price. The instance administrator enters each price under **Settings → Instance → Model prices**, in USD for one million input tokens and one million output tokens. `GET /api/instance/ai/prices` lists the prices and `POST /api/instance/ai/prices` sets one. Only the instance administrator can call them.

| Field | Rule |
|---|---|
| `providerKind` | `openai`, `anthropic`, `gemini` or `openai-compatible`. |
| `model` | 1 to 200 characters. It must be the model name of the provider entry. |
| `inputUsdPerMillionTokens`, `outputUsdPerMillionTokens` | From 0.00000001 to 100000. Vellum stores 8 decimals. A price cannot be zero. |
| `effectiveFrom` | The start date. Empty means from now. The form in the app always sends it empty. |

A call uses the newest price that is in effect when the call starts. A price with the same provider kind, model and start date as a stored price replaces it. No endpoint deletes a price. Vellum writes each change to the audit log as `ai.price.set`.

## Budgets

A provider can have a monthly budget (**Budget per month (USD)** on the provider entry), and a workspace can have one for all providers (**AI budget per month** under **Usage limits**). A budget is empty or from 0 to 1000000.

- Before a call, Vellum reserves the most the call can cost. It refuses the call if the cost of the month plus the reservation passes the budget.
- The reservation is the price of the estimated input tokens plus the price of the most output tokens the call can produce. Vellum estimates one input token for each 2 bytes of the request.
- After the call, Vellum replaces the reservation with the cost from the reported tokens.
- A call that the provider refused before it produced output costs nothing.
- A call that can have produced output and reported no usable token count is charged an estimate. The estimate uses each count that was reported and the figure of the reservation for each count that was not.
- A model with no price cannot be budgeted. Vellum refuses a call under a budget for such a model.

The month is the calendar month in UTC.

## Rate limit

A provider entry can have a limit of requests in one minute (**Requests per minute**, from 1 to 100000). Vellum counts the calls of the provider that started in the last 60 seconds. It refuses a call over the limit and says how long to wait.

See [Connect an AI provider](/how-to/connect-an-ai-provider/) and [Settings](/reference/settings/).
