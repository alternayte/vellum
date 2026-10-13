---
title: What leaves your instance
description: Which data Vellum sends to GitHub and to AI providers, and the controls on it.
---

This page explains which data leaves a Vellum instance and what controls it.

## Nothing, by default

Without an AI provider and without an import, Vellum sends nothing to another service. Modelling, tracing, review rules and export run on your instance.

## GitHub

Vellum sends requests to the GitHub API address in the workspace settings: the repository name, the branch, tag or commit, and file paths. It sends the access token of the workspace only to that host.

Vellum downloads file content. It never uploads anything and never runs the code it downloads.

## AI providers

A task that is routed to a provider sends that provider the instructions of the task and the data the task needs, such as the part of the model you asked about.

Three controls apply to every call:

- **Private repository code.** Text from a private repository goes to a provider only when **Privacy → Send private repository code to AI providers** is on for the workspace and the instance administrator allows it. Both are off in the hosted profile until someone turns them on. With the setting off, Vellum refuses the call and names the setting.
- **Secret removal.** Vellum removes text that looks like a credential before a request leaves: keys, tokens, passwords, connection strings and private keys. This is a best-effort filter. It is not the control that protects private code; the setting above is.
- **Data, not instructions.** Text from a repository or from a model is marked as data in the request. Vellum does not follow instructions that appear in it, and it accepts a reply only when the reply matches the expected structure.

## Where requests can go

An instance can refuse addresses on private networks. The hosted profile refuses them and requires `https` for any address that receives a credential. Vellum checks the address again when it connects, and it does not follow a redirect to another host.

## What Vellum keeps

Vellum records each AI call without the prompt and without the reply. See [AI usage and prices](/reference/ai-usage-and-prices/).
