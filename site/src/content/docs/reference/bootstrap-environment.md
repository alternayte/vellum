---
title: Bootstrap environment
description: The only values Vellum reads from the environment, and when it refuses to start.
---

This page lists the values Vellum reads from the environment. You edit every other setting in the app.

| Variable | Default | What it does |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | none; required | The Postgres connection string. |
| `Vellum__Profile` | `local` | The operating profile: `local` or `hosted`. |
| `Vellum__MasterKey` | none | The master key: the base64 form of 32 random bytes. |
| `Vellum__MasterKeyFile` | none | A file that holds the master key. Set this or `Vellum__MasterKey`, not both. |
| `Vellum__PreviousMasterKey`, `Vellum__PreviousMasterKeyFile` | none | The old key during a rotation. Vellum only reads with it. |
| `Vellum__DataDirectory` | `./data` | Local profile only: where Vellum keeps the master key that it creates (`master.key`) and the setup code (`setup-code`). |
| `Vellum__SourcesRoot` | none | Local profile only: the one folder that a `directory` Source can read. A Source names a folder under it. With no value, Vellum refuses a directory Source with `sources_root_not_configured`. The hosted profile ignores it. See [Import a local folder](/how-to/import-a-local-folder/). |
| `Import__Worker__Parallelism` | `2` | The number of scans that one instance runs at the same time. 1 to 32. See [Scan worker](#scan-worker). |
| `Import__Worker__MaxRunningPerHost` | `2` | The number of scans of one git host that run at the same time, for all instances and workspaces together. 1 to 64. See [Scan worker](#scan-worker). |
| `Vellum__SetupToken` | none | The token that the first administrator types in the setup screen. At least 16 characters. Required in the hosted profile while no user exists. Optional in the local profile. |
| `Vellum__SecureCookies` | `true` in hosted, `false` in local | `true` marks the session cookie as Secure on every response. With `false`, the cookie is Secure only when the request came over HTTPS. |
| `AllowedHosts` | `*` | The public host names of the instance, separated by `;`. |
| `Database__MigrateOnStartup` | `true` | Applies database migrations when Vellum starts. Set `false` when a separate step applies them. |
| `Vellum__TrustedProxies` | none | The addresses or the CIDR ranges of your reverse proxies, separated by `;`, for example `10.0.0.5;172.20.0.0/16`. Vellum reads `X-Forwarded-For` and `X-Forwarded-Proto` from these senders only, and only the last hop. With no value, Vellum ignores the two headers. See [Behind a reverse proxy](#behind-a-reverse-proxy). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | none | The `http` or `https` address of an OTLP collector, for example `http://otel-collector:4317`. With a value, Vellum sends traces, metrics and logs to it. With no value, telemetry is off. See [Telemetry](#telemetry). |
| `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`, `OTEL_TRACES_SAMPLER` | The defaults of the OpenTelemetry SDK. The service name is `vellum`. | The OpenTelemetry SDK reads them. They have an effect only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. |
| `Authentication__GitHub__ClientId`, `…ClientSecret` | none | Optional sign-in with GitHub. |
| `Authentication__Google__ClientId`, `…ClientSecret` | none | Optional sign-in with Google. |

Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Vellum does not need it. The web framework reads it and then trusts the forwarded headers from every sender.

## Docker Compose values

The Compose file reads these values from your shell or from a `.env` file. Vellum itself does not read them.

| Variable | Default | What it does |
|---|---|---|
| `VELLUM_BIND` | `127.0.0.1` | The address on your machine that publishes the app port. Set `0.0.0.0` to accept connections from other machines. |
| `VELLUM_PORT` | `8080` | The app port on your machine. |
| `VELLUM_POSTGRES_BIND` | `127.0.0.1` | The address that publishes the database port. The Compose database has a well-known password. Change this value only on a network that you trust. |
| `VELLUM_POSTGRES_PORT` | `5432` | The database port on your machine. |
| `VELLUM_MASTER_KEY` | empty | Passed to Vellum as `Vellum__MasterKey`. Empty means that Vellum uses `master.key` on the data volume. |
| `VELLUM_PREVIOUS_MASTER_KEY` | empty | Passed to Vellum as `Vellum__PreviousMasterKey`. |
| `VELLUM_SETUP_TOKEN` | empty | Passed to Vellum as `Vellum__SetupToken`. Empty means that Vellum creates a setup code. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Passed to Vellum with the same name. Empty means that telemetry is off. |
| `VELLUM_SOURCES` | `./sources`, next to the Compose file | The folder on your machine that the Compose file mounts read-only at `/sources`. A `directory` Source reads folders under it. A change needs a restart of the container. |

The Compose file sets `Vellum__Profile` to `local`, `Vellum__DataDirectory` to `/data` on the volume `vellum-data`, `Vellum__SourcesRoot` to `/sources`, and the connection string of the Compose database.

The Compose file passes only the variables that it lists. To set another variable of Vellum, for example `Vellum__TrustedProxies` or `OTEL_EXPORTER_OTLP_HEADERS`, add it to the `environment` of the `vellum` service.

The Compose file starts the `vellum` container with a read-only root file system, with no Linux capability and with `no-new-privileges`. The process is not root. It writes to the volume on `/data` and to a temporary directory in memory on `/tmp`. It reads `/sources` and cannot write to it.

### The hosted Compose example

`docker-compose.hosted.example.yml` starts the hosted profile. It reads these values. The database port is not published.

| Variable | Default | What it does |
|---|---|---|
| `VELLUM_MASTER_KEY` | none; required | Passed to Vellum as `Vellum__MasterKey`. |
| `VELLUM_SETUP_TOKEN` | none; required | Passed to Vellum as `Vellum__SetupToken`. |
| `VELLUM_HOSTS` | none; required | Passed to Vellum as `AllowedHosts`. |
| `VELLUM_POSTGRES_PASSWORD` | none; required | The password of the database user. It is also in the connection string that Vellum gets. |
| `VELLUM_TRUSTED_PROXIES` | none; required | Passed to Vellum as `Vellum__TrustedProxies`. |
| `VELLUM_PREVIOUS_MASTER_KEY` | empty | Passed to Vellum as `Vellum__PreviousMasterKey`. |
| `VELLUM_BIND` | `127.0.0.1` | The address on your machine that publishes the app port. |
| `VELLUM_PORT` | `8080` | The app port on your machine. |
| `VELLUM_SUBNET` | `172.30.250.0/24` | The address range of the Compose network. The range is fixed, so that the addresses in `VELLUM_TRUSTED_PROXIES` stay correct after a restart. Set another range when Docker answers `Pool overlaps with other one on this address space`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Passed to Vellum with the same name. |

For a proxy on the Docker host, Vellum sees the gateway of the network. The gateway is the first address of the range: `172.30.250.1` with the default. See [Run in hosted mode](/how-to/run-in-hosted-mode/#start-with-the-hosted-compose-example).

## The two profiles

| | `local` | `hosted` |
|---|---|---|
| Master key | Created in the data directory if you give none | Required |
| First account | The setup screen, with the setup code from the log, or with `Vellum__SetupToken` if you set one | The setup screen, with `Vellum__SetupToken` |
| Self-registration after setup | Off | On |
| URLs on private networks | Allowed | Refused |
| Plain `http` in a settings URL | Allowed | Refused, except for a private host when private network URLs are allowed |
| Sending private code to AI | A workspace can allow it | Refused until the instance administrator allows it |
| `directory` Source | Available when `Vellum__SourcesRoot` names a folder | Refused with `source_kind_not_in_profile` |

## First-run setup

Setup needs proof that the caller operates the instance, in both profiles. `GET /api/setup/status` returns `requiresSetupToken: true` and a `setupTokenHint`:

- `log`: Vellum created a setup code. It is in the log, on the line that contains `VELLUM SETUP CODE:`, and in the file `setup-code` in the data directory. Only the app user can read the file.
- `operator`: the value is the `Vellum__SetupToken` that the operator set.

Vellum creates a setup code when it starts in the local profile with no user and no `Vellum__SetupToken`. It deletes the `setup-code` file when setup is complete.

## When Vellum refuses to start

Vellum stops at start-up, with a message that states the fix, in these cases.

In both profiles:

- `Vellum__Profile` is not `local` or `hosted`.
- A master key value is not the base64 form of exactly 32 bytes.
- `Vellum__MasterKey` and `Vellum__MasterKeyFile` are both set, or the two previous-key values are both set.
- A key file that a `…File` value names cannot be read.
- `Vellum__SecureCookies` is not `true` or `false`.
- `Vellum__SetupToken` is set and is shorter than 16 characters.
- An entry of `Vellum__TrustedProxies` is no address and no CIDR range, or it is a range that holds every address, such as `0.0.0.0/0`.
- `OTEL_EXPORTER_OTLP_ENDPOINT` is set and is not an absolute `http` or `https` URL.
- The database holds stored credentials or session keys under a master key that is not loaded. The message gives the ID of each missing key, the number of rows that use it, and the IDs of the loaded keys. See [Rotate the master key](/how-to/rotate-the-master-key/).

In the local profile, when the environment gives the master key and a `master.key` file also exists, Vellum starts, does not read the file and writes a warning to the log.

In the local profile:

- No master key is set, no `master.key` file exists, and the database holds data under a key. Vellum does not create a new key.
- No master key is set and the database cannot be reached to make that check.
- Users other than the owner can read or change `master.key`. The fix is `chmod 600` on the file.
- `master.key` is empty, or the data directory cannot be written.
- The `setup-code` file cannot be read or created.
- The `setup-code` file is empty or holds fewer than 16 characters. The fix is to delete the file and start again.

In the hosted profile:

- No master key is set.
- `AllowedHosts` is empty, or one entry is `*`, contains `*`, is `0.0.0.0`, `::` or `[::]`, or is empty. The position of the entry in the list does not matter.
- `Vellum__SecureCookies` is `false`.
- No user exists and `Vellum__SetupToken` is not set.

## Commands

The Vellum program accepts these commands in place of a normal start. With Compose, run them as `docker compose run --rm vellum <command>`.

| Command | What it does |
|---|---|
| `dotnet Vellum.dll keys forget` | Lists the stored credentials and session keys that use a master key that the environment does not give. It deletes nothing and exits with code 1 if it lists any. |
| `dotnet Vellum.dll keys forget --yes` | Deletes those rows and records the deletion in the audit log. Use it only when the key is lost. |
| `dotnet Vellum.dll keys` with other arguments | Prints the usage line and exits with code 2. |
| `dotnet Vellum.dll health` | Calls `/health` on `127.0.0.1`, on the first port in `ASPNETCORE_HTTP_PORTS` (`8080` when the value is not set). It exits with code 0 when the answer is `200`. The container health check runs it. |

## Limits on sign-in and setup

Each of these limits counts for one client address in one minute. Over a limit, the answer is `429` with the type `rate_limited` and a `Retry-After` header.

| Requests | Limit |
|---|---|
| `POST /api/auth/login` | 10 calls |
| `POST /api/setup` | 10 calls |
| `GET /api/auth/external/{provider}` and `GET /api/auth/external/complete` | 10 calls for each endpoint |
| `POST /api/auth/register` | 10 calls |
| Each request with no valid credential to `/api`, `/mcp`, `/llms.txt`, `/llms-full.txt` or `/skills` | 300 calls together |

A request with a valid session or a valid agent token does not count for the limit of 300. The health endpoints and the files of the web client do not count. No setting changes these numbers.

Five wrong passwords lock an account for five minutes. Sign-in answers `401` for a wrong password, for an unknown account and for a locked account.

A new password has 10 characters or more, with one digit or more and one upper-case letter or more.

A request body that is larger than its limit gets `413` before Vellum reads it: 4 KB for sign-in and setup, 32 KB for a credential, 64 KB for workspace settings and 68 KB for instance settings.

## Behind a reverse proxy

`Vellum__TrustedProxies` names the senders whose `X-Forwarded-For` and `X-Forwarded-Proto` headers Vellum reads.

| Item | Rule |
|---|---|
| Format | Addresses or CIDR ranges, separated by `;`. Spaces around an entry and empty entries are ignored. |
| Not accepted | An entry that is no address and no range. A range with the prefix length 0, which holds every address. Vellum then refuses to start. |
| A trusted sender | Vellum takes the client address from the last entry of `X-Forwarded-For` and the scheme from `X-Forwarded-Proto`. It reads one hop only. |
| Each other sender | Vellum ignores the two headers. The client address is the address of the connection. |
| No value | Vellum ignores the two headers from every sender. |

The client address counts for the rate limits. The scheme decides whether the session cookie is Secure in the local profile, and whether the hosted profile sends `Strict-Transport-Security`.

## Sign-in with GitHub or Google

Vellum offers a provider only when the instance has its client ID and its client secret. `GET /api/auth/external/{provider}` answers `404` with the title `This sign-in provider is not configured` for each other name.

| Item | GitHub |
|---|---|
| Callback URL to register at GitHub | `https://<your-host>/api/auth/external/callback` |
| Endpoint of Vellum that completes the sign-in | `GET /api/auth/external/complete`. Vellum sends the browser there. Do not register it at GitHub. |
| Scope | `user:email` |
| Email address | The primary address of the account, and only when GitHub verified it. With no such address the answer is `400` with the title `The provider gave no verified email address`. |

A first sign-in with a provider creates an account, if the instance allows registration. It never creates the first account; only setup does.

If an account with the same email address already exists, Vellum refuses the sign-in with `409`. Vellum does not link a provider to an existing account. Sign in with the password of that account.

## Health

| Endpoint | `200` | `503` | Probe |
|---|---|---|---|
| `GET /health` | The app reaches its database. | It does not. | One probe for both questions: a load balancer, and the container health check. |
| `GET /health/ready` | The app reaches its database. | It does not. | Readiness. |
| `GET /health/live` | The process answers. The database is not asked. | Never. | Liveness. A database outage must not restart the container. |

Each endpoint needs no sign-in and gives no detail. The body is the status word. The instance administrator reads details under **Settings → Instance → Diagnostics**.

The container image and the Compose files have a health check. `docker compose ps` shows `healthy` when `/health` returns `200`.

## Scan worker

The scan worker takes queued scans from the one scan queue in the database.

| Item | Rule |
|---|---|
| `Import__Worker__Parallelism` | The number of scans that one instance runs at the same time. A value outside 1 to 32 is moved to the nearest end. |
| `Import__Worker__MaxRunningPerHost` | The number of scans of Sources on one git host that run at the same time, for all instances and all workspaces together. Other scans of that host wait in the queue. A value outside 1 to 64 is moved to the nearest end. |
| Folder and archive scans | They read no git host. `Import__Worker__MaxRunningPerHost` does not count them. |

A bulk add queues many scans of one host. The limit for each host keeps them from using up the rate limit of the host together. See [Add many repositories](/how-to/add-many-repositories/#watch-the-scans).

## Telemetry

Vellum exports traces, metrics and logs through OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. With no value, Vellum sends no telemetry to another machine.

A value that is not an absolute `http` or `https` URL stops the start. A change needs a restart.

The instance administrator reads the state under **Settings → Instance → Telemetry**. [Diagnostics and logs](/runbooks/diagnostics-and-logs/#telemetry) lists what Vellum exports and what it removes first.
