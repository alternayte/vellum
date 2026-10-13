---
title: What leaves your instance
description: Which data Vellum sends to GitHub, to AI providers and to a telemetry collector, and the controls on it.
---

This page explains which data leaves a Vellum instance and what controls it.

## Nothing, by default

Vellum sends a request to another service only for these functions: a GitHub read, an AI task and an Azure price search. Modelling, tracing, review rules, cost estimates, stress runs and export run on your instance.

Vellum generates a k6 script. It does not run the script and it sends no load to a system.

Vellum sends no telemetry by default. See [Telemetry](#telemetry).

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

## Telemetry

Telemetry is the traces, the metrics and the logs of the program. It leaves the instance only when the operator sets the environment variable `OTEL_EXPORTER_OTLP_ENDPOINT`. Vellum then sends it to the OTLP collector at that address, and to no other place. With no value, Vellum sends no telemetry to another machine.

The operator selects the collector. Vellum has no collector of its own, and no telemetry goes to the makers of Vellum.

No screen and no API call turns telemetry on. The instance administrator reads the state under **Settings → Instance → Telemetry**. The panel shows **Off. This instance sends no traces, metrics or logs to another machine.** or **On. Traces, metrics and logs go to** with the host name of the collector.

Vellum removes these things before telemetry leaves the process:

- The query string of each request and of each URL in a log line.
- The path of each outbound address. The scheme, the host and the port stay.
- Text that looks like a credential, in each text value of a span and of a log record.

A span holds no request header and no request body. The tags of the counters are short fixed words. They hold no workspace, no user and no text from a request.

Telemetry holds no prompt, no reply and no model data on purpose. But a log line can hold a name from your model, for example in an error message, and the credential filter does not find a secret that has no known shape. Send telemetry only to a collector that you trust. [Diagnostics and logs](/runbooks/diagnostics-and-logs/#telemetry) lists the signals and the counters.

## What Vellum keeps

Vellum records each AI call without the prompt and without the reply. See [AI usage and prices](/reference/ai-usage-and-prices/). The log lines, the span and the metrics that Vellum writes for an outbound request hold a provider label, an operation label, the status class and the duration. They hold no address, no header and no body. With telemetry on, the HTTP client of the framework also writes a span for the request. That span holds the scheme, the host and the port of the address, and no path.

See [Security model](/concepts/security-model/) for the controls on what comes in.
