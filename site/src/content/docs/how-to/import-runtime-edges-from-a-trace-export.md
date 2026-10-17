---
title: Import runtime edges from a trace export
description: Upload an OTLP JSON, Jaeger or Tempo trace export, review the measured call and messaging edges it shows, and accept them into the model.
---

This guide shows you how to turn a trace export into proposed services and edges. Vellum labels each one `measured`. Nothing reaches the model until you accept it.

A trace Source shows which services called which, and which services sent messages to which, in the window of the export. It does not show latency. It does not show an edge that no span in the file shows.

## Before you start

- You need the Editor or the Owner role in the project.
- You need a trace export in one of these formats:
  - **OTLP JSON**: an `ExportTraceServiceRequest` as JSON, with `resourceSpans` at the top. The OpenTelemetry Collector file exporter writes this form.
  - **Jaeger JSON**: the file that **Download JSON** in the Jaeger UI gives, or the answer of the Jaeger query API, with `data[].spans` and `data[].processes`.
  - **Tempo JSON**: the OTLP shape that Tempo gives, under `batches` or `trace.resourceSpans`.
- The file is at most 256 MiB, holds at most 1,000,000 spans, and shows at most 2,000 services and 10,000 edges. Vellum refuses a larger file as a whole. Export a shorter window.

## Add a trace Source and upload a file

1. Open the project.
2. Select **Sources** in the sidebar.
3. Under **Sources**, select **Add runtime Source**.
4. Select **Trace file**.
5. Type a **Name**, such as `checkout traces`.
6. Select the file under **Trace export**.
7. Select **Add and read file**.

Vellum reads the file as it arrives. The dialog shows how many spans it read, how many services and edges it found, and the Snapshot ID. The file itself is not stored.

To read another export into the same Source, select **Upload trace file** on the Source. Each upload is a new Snapshot of the Source.

With the HTTP API, send the file as the request body:

```bash
curl -X POST "http://localhost:8080/api/projects/$PROJECT/sources" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"kind":"traces","name":"checkout traces"}'

curl -X POST "http://localhost:8080/api/projects/$PROJECT/sources/$SOURCE/traces?fileName=export.json" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  --data-binary @export.json
```

The upload answers `202` with the scan ID. A refused file answers `400` (malformed, of no known shape, or with no span) or `413` (over a bound), and Vellum stores nothing of it.

## Know what becomes an edge

- **A call**: a CLIENT span with a SERVER child span in another service. The edge goes from the service of the client to the service of the server.
- **A call to a peer**: a CLIENT span that no SERVER span answers. The edge goes to the service that `peer.service` names, else to the host that `server.address` names. The service shows `seenAs: peer`.
- **A message**: a PRODUCER span that a CONSUMER span of another service links to, or has as its parent. The edge goes from the producer to the consumer and has the label `sends messages to`.

Spans of one service never make an edge. A producer with no consumer in the file makes no edge; the scan notes how many there were.

Each edge lists its operation names (at most 50) and has these sample values: the number of calls, the number of calls with an error status, and the first and last time it was seen. Every fact also carries the file name, the span count and the window of the export as `sample.file`, `sample.spans` and `sample.window`. A sample value never makes a change proposal: the next export has other counts and says nothing new about the architecture.

For the full list of the fields Vellum keeps and drops, see [Runtime sources](/reference/runtime-sources/).

## Review and accept

1. Select **Review** on the Source.
2. Each service is an `app` element. They sit in a `system` element named after the Source. Each edge is a behavioural relationship.
3. Select a proposal to see its values and its citation. The citation reads `traces/<source>@<snapshot>:traces/<file>:<line>-<line>`. The cited line is the derived line that Vellum kept for the fact.
4. Select the proposals and select **Accept**.

An accepted fact is labelled `measured`. Its provenance names the Source, the Snapshot, the file and the line.

When a service of the export has the name of an element that another Source holds, such as a service that a code scan found, the review asks **Same as?**. Answer **Same** to bind the element to the trace service. Vellum never joins two elements by name alone.

## Read a later export

Upload the next export to the same Source. A complete read looks at the whole Source, so an accepted edge that the new export does not show gets a stale mark and a removal proposal. The mark deletes nothing. Upload an export of the same scope as the first one, or the edges of services outside it show as stale.
