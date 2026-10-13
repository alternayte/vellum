---
title: What leaves your instance
description: Which data Vellum sends to GitHub and to AI providers, and the controls on it.
---

This page explains which data leaves a Vellum instance and what controls it.

## Nothing, by default

Vellum sends a request to another service only for these functions: a GitHub read, an AI task and an Azure price search. Modelling, tracing, review rules, cost estimates, stress runs and export run on your instance.

Vellum generates a k6 script. It does not run the script and it sends no load to a system.

## GitHub

Vellum sends requests to the GitHub API address in the workspace settings (`github.apiBaseUrl`). A request holds the repository name, the branch, tag or commit, or the ID of a file or of a workflow run. Vellum sends the access token of the workspace only to that host.

Vellum reads GitHub for an import scan, a connection test, and the check of a GitHub Actions run that is recorded as evidence. Every request is a read. Vellum never uploads anything and never runs the code it downloads.

GitHub redirects a repository that was renamed or moved. Vellum follows such a redirect only to the same scheme, host and port as the API address, at most three times.

## AI providers

A task that is routed to a provider sends that provider the instructions of the task and the data the task needs, such as the part of the model you asked about. Vellum sends the API key of a provider only to the host that the key was saved for.

Three controls apply to every call:

- **Private repository code.** Text that came from a private repository goes to a provider only when **Privacy → Send private repository code to AI providers** is on for the workspace and the instance policy allows it. The workspace setting is off by default. The instance policy (**Settings → Instance → Let workspaces send private code to AI**) is on in the local profile and off in the hosted profile until the instance administrator changes it. With the setting off, Vellum leaves the private-sourced parts of the model out of the prompt and reports how many it left out. Vellum refuses a request that still holds private text, and the refusal names the setting `privacy.sendPrivateCodeToAi`.
- **Secret removal.** Vellum removes text that looks like a credential before a request leaves: keys, tokens, passwords, connection strings and private keys. This is a best-effort filter. It does not find a credential that has no known shape and no name beside it. It is not the control that protects private code; the setting above is.
- **Data, not instructions.** Text from a repository or from a model is marked as data in the request. Vellum does not follow instructions that appear in it, and it accepts a structured reply only when the reply matches the expected structure.

A part of the model is private-sourced when an import found it in a repository that the scan did not record as public, and the part does not have the label `declared`. A scan that could not tell counts as private.

## Azure prices

A price search under **Settings → Prices** sends a request to the Azure Retail Prices address in the workspace settings (`cost.azureRetailPricesBaseUrl`). The default is `https://prices.azure.com`. The request holds the search terms and the currency. Vellum sends no credential to this address. An empty address turns the search off. See [Cost model](/reference/cost-model/).

## Where requests can go

The instance policy **Allow URLs on private networks** controls addresses on private networks. The hosted profile refuses them by default and requires `https`. The local profile allows them by default. Vellum checks the address on every attempt and connects only to the addresses that it checked. Vellum does not follow a redirect of an AI provider.

Vellum ignores the proxy settings of the machine. An instance that can reach the internet only through a proxy cannot reach a provider.

## What Vellum keeps

Vellum records each AI call without the prompt and without the reply. See [AI usage and prices](/reference/ai-usage-and-prices/). The logs and the telemetry of an outbound request hold a provider label, an operation label, the status class and the duration. They hold no address, no header and no body.

See [Security model](/concepts/security-model/) for the controls on what comes in.
