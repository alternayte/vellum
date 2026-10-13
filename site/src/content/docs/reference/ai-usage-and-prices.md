---
title: AI usage and prices
description: What Vellum records for each AI call, how it estimates cost and how budgets work.
---

This page describes the usage record, the price table and the budget rules.

## What a usage record holds

Vellum writes one record for each call to a provider.

| Field | Content |
|---|---|
| Provider and model | The configured provider, its kind, the model name and the version the provider reports. |
| Task and purpose | The task that made the call, and a short purpose such as `test-connection`. |
| Prompt template version | The version of the prompt template that built the request. |
| Tokens | The input and output counts the provider reports. Empty when it reports none. |
| Estimated cost | In USD. Empty when the model has no price. |
| Outcome | Succeeded, refused, invalid output, rate limited, failed, timed out, cancelled or abandoned. |
| Duration, time, account | When the call ran, how long it took and who started it. |

A record never holds the prompt or the reply.

## Read the usage

Open **Settings → Usage limits**. The table shows the calls of the current month for each provider, model and task.

- **unknown** means the model has no price.
- **at least** means some calls of the row have no known cost.
- **up to** means the provider reported no token count. Vellum then counts the most the call could cost.

`GET /api/workspaces/{workspaceId}/ai/usage?from=&to=` returns the same totals. The range is at most 366 days.

## Prices

Vellum ships no price. The instance administrator enters each price under **Settings → Instance → Model prices**, in USD for one million input tokens and one million output tokens.

A price has a start date. A call uses the newest price that is in effect when the call starts. A price cannot be zero.

## Budgets

A provider can have a monthly budget, and a workspace can have one for all providers.

- Before a call, Vellum reserves the most the call can cost. It refuses the call if the reservation passes the budget.
- After the call, Vellum replaces the reservation with the cost from the reported tokens.
- A call that the provider refused before it produced output costs nothing.
- A call that started to produce output and reported no token count is charged the reserved amount.
- A model with no price cannot be budgeted. Vellum refuses a call under a budget for such a model.

The month is the calendar month in UTC.
