---
title: Runtime sources
description: The trace fields Vellum keeps and drops, the derived document of a runtime Snapshot, the NerdGraph queries of a New Relic Source, and the bounds of both.
---

This page lists exactly what a `traces` Source and a `newrelic` Source read and keep.

## Trace fields

Vellum reads a trace export as a stream of JSON tokens. For each span it holds these values in memory until the end of the read, and no other value:

| Value | OTLP and Tempo | Jaeger |
| --- | --- | --- |
| Trace ID, span ID | `traceId`, `spanId` | `traceID`, `spanID` |
| Parent | `parentSpanId` | `parentSpanID`, or a `CHILD_OF` reference |
| Links | `links[].traceId`, `links[].spanId` | `FOLLOWS_FROM` references |
| Service | resource attribute `service.name` | `processes[].serviceName` or `process.serviceName` |
| Operation | `name` | `operationName` |
| Span kind | `kind` (number or `SPAN_KIND_*`) | tag `span.kind` |
| Peer | attribute `peer.service`, else `server.address` | tag `peer.service`, else `server.address` |
| Status | `status.code` is error | tag `error` is true, or `otel.status_code` is `ERROR` |
| Time | `startTimeUnixNano`, `endTimeUnixNano` | `startTime`, `duration` |

Vellum drops everything else: every other span attribute and tag, every event and log, the status message, every other resource attribute, the scope, and the span IDs themselves once the edges are derived. It keeps no raw span. The uploaded file is not written anywhere.

A service, operation or peer name with a control character, an e-mail address or the shape of a credential is not kept. A span whose service name is not kept is dropped; the header counts them as `spansDropped`. An operation name that is not kept is counted as `operationsRedacted`. A name is cut at 200 characters.

## The derived document

The only file of a runtime Snapshot is a document that Vellum writes: one JSON object per line.

- Line 1 is the header: `format` (`vellum.runtime.v1`), `complete`, `kind`, and for traces `file`, `exportFormat`, `spans` and `window`; for New Relic `account`, `region`, `entities` and `relationships`.
- Then one line per service, ordered by key: `type` `service`, `key`, `name`, `truthLabel` and its attributes.
- Then one line per edge, ordered by key: `type` `interaction`, `key`, `from`, `to`, `label`, `truthLabel` and its attributes.

A citation names the line of its fact. The Snapshot ID is the SHA-256 of the lines after the header. The same services and edges give the same Snapshot ID, whatever the format of the export and whatever the file name.

| Fact | Key | External key |
| --- | --- | --- |
| Trace service | `service:<name>` | the key |
| Trace call edge | `call:<from>-><to>` | — |
| Trace messaging edge | `messaging:<from>-><to>` | — |
| New Relic entity | its GUID | the GUID |
| New Relic relationship | `<TYPE>:<source GUID>-><target GUID>` | — |

## Bounds of a trace upload

| Bound | Value | On reaching it |
| --- | --- | --- |
| File size | 256 MiB | `413 trace_too_large` |
| Spans | 1,000,000 | `413 trace_too_many_spans` |
| Services | 2,000 | `413 trace_too_many_services` |
| Edges | 10,000 | `413 trace_too_many_edges` |
| One JSON value | 1 MiB | `413 trace_token_too_large` |
| Nesting depth | 64 | `400 trace_malformed` |
| Operations of one edge | 50 | the rest are counted in `operationsLeftOut` |

A malformed file gives `400 trace_malformed`, a file of no known shape `400 trace_format_unknown`, and a file with no span `400 trace_empty`. A refused file stores nothing.

## NerdGraph queries

A New Relic Source sends these queries to `https://api.newrelic.com/graphql` (US) or `https://api.eu.newrelic.com/graphql` (EU), with the key in the `API-Key` header. No setting and no request names another address. It sends no mutation.

The entity search, paged with `nextCursor`. The search text is `accountId = <id> AND domainType IN ('APM-APPLICATION', 'EXT-SERVICE', 'BROWSER-APPLICATION', 'MOBILE-APPLICATION')`:

```graphql
query($query: String!, $cursor: String) {
  actor {
    entitySearch(query: $query) {
      results(cursor: $cursor) {
        nextCursor
        entities { guid name domain type entityType }
      }
    }
  }
}
```

The relationships of 25 entities at a time:

```graphql
query($guids: [EntityGuid]!) {
  actor {
    entities(guids: $guids) {
      guid
      relatedEntities {
        nextCursor
        results {
          type
          source { entity { guid name domain type entityType } }
          target { entity { guid name domain type entityType } }
        }
      }
    }
  }
}
```

Each further page of one entity's relationships uses `entity(guid: $guid) { relatedEntities(cursor: $cursor) { … } }` with the same fields. **Test connection** sends `actor { account(id: $accountId) { id name } }`.

| Bound | Value |
| --- | --- |
| Entities | 500 |
| Relationships of one entity | 500 |
| Relationships in all | 20,000 |

A read that reaches a bound, or that gets an answer with both data and errors, is partial. A partial read marks nothing stale.

| Answer | Result |
| --- | --- |
| `401` or `403`, or an `UNAUTHORIZED`, `FORBIDDEN` or `ACCESS_DENIED` error | The scan fails with `newrelic_unauthorized`. |
| `429`, or a `TOO_MANY_REQUESTS` error | The scan waits until `Retry-After`, or one minute. |
| No connection or a timeout | The scan fails with `newrelic_unavailable`. A resume tries again. |
| `5xx` | The scan fails with `newrelic_error`. A resume tries again. |
