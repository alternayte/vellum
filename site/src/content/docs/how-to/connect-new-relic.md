---
title: Connect New Relic
description: Read the entities of a New Relic account and the relationships between them through NerdGraph, with a read-only key at the fixed US or EU endpoint.
---

This guide shows you how to add a New Relic account as a Source. Vellum reads the entities of the account and the relationships between them, and proposes them as elements and relationships. It reads no metrics, traces, logs or attributes.

## Before you start

- You need the Owner role in the workspace. The Source stores a credential.
- You need the account ID and the region (US or EU) of the New Relic account.
- You need a New Relic **User API key** of a user who can read the account. Give that user read access only. Vellum sends queries only, never a mutation.
- The instance needs network access to `https://api.newrelic.com` (US) or `https://api.eu.newrelic.com` (EU). Without it the Source shows as unavailable, and every other feature works.

## Add the Source

1. Open the project.
2. Select **Import from GitHub** in the left toolbar.
3. Under **Sources**, select **Add runtime Source**.
4. Select **New Relic**.
5. Type the **Account ID**, select the **Region** and type the **User API key**. The **Name** is optional.
6. Select **Test connection**. Vellum sends one query to the endpoint of the region and shows the account name, or what to change.
7. Select **Add and read account**.

Vellum stores the key envelope-encrypted and bound to the endpoint of the region. No screen and no API answer shows it again. The audit log records `secret.set` with the name `newrelic:<source id>:<region>:apiKey`.

The Source lists the account, the region and whether a key is stored. Its status line says if the last read is complete, partial, waiting for the rate limit, unavailable, or failed.

## Know what Vellum reads

Vellum reads these entity types of the account: APM applications, OpenTelemetry and other external services (`EXT-SERVICE`), browser applications and mobile applications. It reads at most 500 entities.

For each entity it reads the relationships (at most 500 for one entity and 20,000 in all). A relationship of type `CALLS`, `PRODUCES` or `CONSUMES` between two entities that the Source reads becomes a relationship. Other types, and relationships to entities that the Source does not read, such as a Kafka topic, are not proposed.

The queries are in [Runtime sources](/reference/runtime-sources/).

## Know the labels

- An **entity** becomes an `app` element labelled `measured`: New Relic makes an entity from what its agents report. Its GUID is the external key of the source binding.
- A **relationship** is labelled `declared`. New Relic makes some relationships itself and lets users make others. The NerdGraph answer that its documentation describes does not say which, so Vellum labels every relationship `declared`.

When an entity has the name of an element that another Source holds, such as a service that a code scan found, the review asks **Same as?**. Answer **Same** to bind the element to the GUID. Vellum never joins two elements by name alone.

## Read the account again

Select **Read again** on the Source. Each read is a new Snapshot. Its ID is the SHA-256 of the sorted entities and relationships.

A complete read that no longer finds an accepted entity or relationship gives it a stale mark and a removal proposal. A partial read concludes nothing from an absence. A read is partial when New Relic answers part of a query with errors or a bound is reached.

## When a read stops

| Status | Cause | What to do |
| --- | --- | --- |
| Waiting for the rate limit | New Relic answered `429` or a `TOO_MANY_REQUESTS` error | Nothing. The scan starts again at the time shown. |
| `newrelic_unauthorized` | New Relic refused the key, or the key cannot read the account | Select **Replace key**, type a valid key, then resume the scan. |
| `newrelic_no_key` | No key is stored, or the master key cannot decrypt it | Select **Replace key**, then resume the scan. |
| Unavailable (`newrelic_unavailable`) | The instance cannot reach New Relic | Resume the scan when the connection works. |

To replace the key with the HTTP API, send `PUT /api/projects/{projectId}/sources/{sourceId}/newrelic/key` with `{"apiKey": "..."}`.
