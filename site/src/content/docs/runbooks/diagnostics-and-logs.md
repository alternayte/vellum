---
title: Diagnostics and logs
description: "Where an operator reads the state of an instance: health, diagnostics, logs and the audit logs."
---

This runbook is for the operator of an instance. It lists each place that reports the state of Vellum, and what each place tells you.

## Health

`GET /health` answers `200` when the app reaches its database, and `503` when it does not. It needs no sign-in and gives no detail.

<!-- snippet: live -->
```sh
curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health"
```

The container has a health check that runs `dotnet Vellum.dll health`. `docker compose ps` shows its result:

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
| `migrations` | For each module: the migrations that are applied and the migrations that are pending. After a start, `pending` is empty for every module. |
| `eventStore` | Whether the event store is reachable, the position of its newest event, and its consumers. |
| `masterKeyId` | The ID of the master key that encrypts new data. The ID is not the key. |
| `previousMasterKeyLoaded` | `true` during a key rotation. |
| `masterKeysInUse` | For each master key that stored data uses: the number of stored credentials and session keys, whether it is the current key, and whether it is loaded. After a complete rotation the list holds the current key only. |

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
| `Cannot load library libgssapi_krb5.so.2` | The database driver looked for Kerberos support, which the image does not have. Vellum does not use it. Ignore the line. |
| `The WebRootPath was not found` | The image was built with no web client. Build it with the `Dockerfile` of the repository. |

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

- No metrics endpoint and no trace export. Monitor the instance with `/health` and with the container log.
- No log file. To keep logs, configure a log driver for the container.
- No alert. A paused scan, a failed scan and a reached budget show in the app only.

## See also

- [Bootstrap environment](/reference/bootstrap-environment/)
- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Back up and restore](/runbooks/backup-and-restore/)
