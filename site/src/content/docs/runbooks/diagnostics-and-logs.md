---
title: Diagnostics and logs
description: "Where an operator reads the state of an instance: health, diagnostics, logs, telemetry and the audit logs."
---

This runbook is for the operator of an instance. It lists each place that reports the state of Vellum, and what each place tells you.

## Health

`GET /health` answers `200` when the app reaches its database, and `503` when it does not. It needs no sign-in and gives no detail.

<!-- snippet: live -->
```sh
curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health"
```

Two more endpoints answer the two questions of an orchestrator apart. They also need no sign-in and give no detail.

| Endpoint | Answers `200` when | Probe |
|---|---|---|
| `GET /health` | The app reaches its database. | One probe for both questions: a load balancer, and the container health check. |
| `GET /health/ready` | The app reaches its database. | Readiness. The orchestrator sends no request to an instance that is not ready. |
| `GET /health/live` | The process answers. The database is not asked. | Liveness. The orchestrator restarts an instance that is not live. |

Use `/health/live` for a liveness probe, not `/health`. A restart does not repair a database outage.

<!-- snippet: live -->
```sh
curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health/live"
curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health/ready"
```

The container has a health check that runs `dotnet Vellum.dll health`. The command calls `/health`. `docker compose ps` shows its result:

<!-- snippet: live -->
```sh
docker compose ps
```

| Status | Meaning |
|---|---|
| `Up (healthy)` | `/health` answers `200`. |
| `Up (health: starting)` | The container started less than 40 seconds ago, and no check passed yet. |
| `Up (unhealthy)` | Five checks in a row failed. Read the log. |
| `Exited (1)` | Vellum refused to start. The last lines of the log give the reason. |

## Diagnostics

The instance administrator reads the diagnostics in the app: **Settings**, then **Instance**, then **Diagnostics**. `GET /api/instance/diagnostics` gives the same data to a signed-in instance administrator.

| Field | What it tells you |
|---|---|
| `profile` | `local` or `hosted`. |
| `version` | The version of the program. |
| `database.reachable` | `false` when the app cannot open a connection. |
| `migrations` | One entry, `vellum`: the migrations that are applied and the migrations that are pending. After a start, `pending` is empty. |
| `eventStore` | Whether the event store is reachable, the position of its newest event, and its consumers. |
| `masterKeyId` | The ID of the master key that encrypts new data. The ID is not the key. |
| `previousMasterKeyLoaded` | `true` during a key rotation. |
| `masterKeysInUse` | For each master key that stored data uses: the number of stored credentials and session keys, whether it is the current key, and whether it is loaded. After a complete rotation the list holds the current key only. |
| `telemetry` | `enabled` is `true` when the instance exports telemetry. `endpointHost` is the host name of the collector, or `null`. The path, the port and the headers of the collector address are not shown, because they can hold a credential. |

The diagnostics hold no secret and no model data.

## Logs

Vellum writes its log to the standard output of the container.

<!-- snippet: live -->
```sh
docker compose logs --tail 100 vellum
```

To follow the log, add `-f`. To read the log of the database, name `postgres` in place of `vellum`.

### Log level

The default level is `Information`, and `Warning` for the ASP.NET Core framework. Set another level with an environment variable of the `vellum` service, for example `Logging__LogLevel__Default=Warning`. The Compose file passes only the variables that it lists, so add the variable to the `environment` of the service.

At the default level, the log holds each SQL command that the app runs, with no parameter values.

### Lines to know

| Line | Meaning |
|---|---|
| `VELLUM SETUP CODE: …` | The instance has no account. The code is for the setup screen. Vellum writes the line on each start until setup is complete. |
| `Vellum did not start. …` | A start-up refusal. The sentence states the cause and the fix. See [Vellum does not start](/troubleshooting/start-up-refusals/). |
| `Cannot load library libgssapi_krb5.so.2` | The database driver looked for the GSSAPI library and the machine does not have it. The image from the `Dockerfile` of the repository has the library and does not write this line. In another environment, install the package `libgssapi-krb5-2`, or ignore the line: Vellum does not use Kerberos. |
| `The WebRootPath was not found` | The image was built with no web client. Build it with the `Dockerfile` of the repository. |

## Telemetry

Telemetry is off by default. Vellum then sends no traces, no metrics and no logs to another machine.

### Turn it on

1. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to the `http` or `https` address of an OTLP collector, for example `http://otel-collector:4317`. Both Compose files pass this variable to Vellum.
2. If the collector needs them, set `OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_EXPORTER_OTLP_HEADERS`. The Compose files do not pass these two. Add them to the `environment` of the `vellum` service.
3. Start Vellum again.

To turn telemetry off, remove the variable and start again. A value that is not an absolute `http` or `https` URL stops the start.

`OTEL_SERVICE_NAME` and `OTEL_TRACES_SAMPLER` apply as the OpenTelemetry SDK reads them. With no `OTEL_SERVICE_NAME`, the service name is `vellum` and the service version is the version of the program.

### Read the state

The instance administrator opens **Settings**, then **Instance**, then **Telemetry**. The panel shows one of these lines.

- **Off. This instance sends no traces, metrics or logs to another machine.**
- **On. Traces, metrics and logs go to** and the host name of the collector.

After each line the panel says: **Set the environment variable OTEL_EXPORTER_OTLP_ENDPOINT and restart Vellum to change this.** No screen and no API call changes the state.

`GET /api/instance/diagnostics` gives the same state in the field `telemetry`.

### What Vellum exports

| Signal | Content |
|---|---|
| Traces | One span for each request to Vellum, for each outbound HTTP request and for each database command of a request. |
| Metrics | The request, HTTP client and database metrics of the framework and of the database driver, and the counters below. |
| Logs | The log records that the program writes. The container log still gets its lines. |

Vellum exports no span for a request to a health endpoint. It exports no span for a database command that belongs to no request, such as the polling of the event store. The database metrics still count those commands.

### The counters

| Name | Counts | Tags |
|---|---|---|
| `vellum.import.scans` | Import scans that ended. | `outcome` |
| `vellum.ai.calls` | Calls to AI providers that ended. | `provider`: the provider kind. `outcome`: see [AI usage and prices](/reference/ai-usage-and-prices/). |
| `vellum.ai.tokens` | Tokens that AI providers reported. | `provider`. `direction`: `input` or `output`. |
| `vellum.stress.runs` | Stress runs that somebody asked for. | `outcome`: `stored`, `refused`, `too_large`, `busy` or `rate_limited`. |
| `vellum.eventstore.append_conflicts` | Writes that lost to another writer of the same stream. | none |
| `vellum.outbound.blocked` | Outbound requests that the URL rules refused. | `provider` |
| `vellum.auth.login_failures` | Password sign-ins that Vellum refused. | `reason`: `invalid_credentials` or `locked_out`. |
| `vellum.outbound.requests` | Outbound requests. | `provider`, `operation`, `status_class` |
| `vellum.outbound.duration` | The duration of each outbound request in milliseconds. This is a histogram. | `provider`, `operation`, `status_class` |

Each tag is a short fixed word. No tag holds a workspace, a user, a URL, a model name that a user typed or text from a request.

### What Vellum removes first

Vellum changes each span and each log record before it leaves the process.

| In | Removed or replaced |
|---|---|
| A span | The query string of the request. |
| A span of an outbound request | The path and the query of the address. The scheme, the host and the port stay. A path can name a repository, a file or a model. |
| A log record | The query string of each URL and each path. The text `?[query]` replaces it. |
| A log record with an exception | The exception object. Its type, its message and its stack trace replace it, as text. |
| Each text value of a span or a log record | Text that looks like a credential. This is the same filter that cleans a prompt before it goes to an AI provider. |

Vellum records no request header and no request body in a span. A log record carries no scope of the framework.

The credential filter works on the shape of a value. It does not find a secret that has no known shape. Send telemetry only to a collector that you trust.

## Audit logs

The audit logs record who changed what. They are in the database, not in the container log.

| Log | Who reads it | Where |
|---|---|---|
| Workspace audit log | An owner of the workspace | Workspace **Settings**, then **Audit log**. `GET /api/workspaces/{workspaceId}/audit` |
| Instance audit log | The instance administrator | `GET /api/instance/audit` |

An entry has the time, the account, the action and the target. An entry holds no secret value. [Configure a workspace](/how-to/configure-settings/#read-the-audit-log) lists what an entry holds.

## AI use

**Settings**, then **AI providers** shows **AI use this month** for a workspace: the calls, the tokens and the estimated cost for each provider and model. `GET /api/workspaces/{workspaceId}/ai/usage` gives the same data. See [AI usage and prices](/reference/ai-usage-and-prices/).

## What Vellum does not have

- No metrics endpoint that a collector reads. Vellum sends metrics, traces and logs to an OTLP collector, and only when you set `OTEL_EXPORTER_OTLP_ENDPOINT`. See [Telemetry](#telemetry). Without it, monitor the instance with the health endpoints and with the container log.
- No log file. To keep logs, configure a log driver for the container.
- No alert. A paused scan, a failed scan and a reached budget show in the app only.

## See also

- [Bootstrap environment](/reference/bootstrap-environment/)
- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Back up and restore](/runbooks/backup-and-restore/)
