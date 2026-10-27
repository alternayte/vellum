---
title: Run Vellum in hosted mode
description: Start Vellum with the hosted profile, with the environment it requires, behind HTTPS and with more than one instance.
---

This guide shows you how to start Vellum with the hosted profile. The hosted profile is for an instance that more than one team reaches over a public address.

## Know what the profile changes

| | `local` | `hosted` |
|---|---|---|
| Master key | Vellum creates `master.key` in the data directory if you give none. | You must give the key. |
| Session cookie | Secure only when the request came over HTTPS. | Always Secure. `Vellum__SecureCookies=false` is refused. |
| `Strict-Transport-Security` header | Not sent. | Sent with each response to a request that came over HTTPS. |
| `AllowedHosts` | Each host name is accepted. | You must list the public host names. |
| First account | The setup code from the log, or `Vellum__SetupToken`. | `Vellum__SetupToken` only. |
| Self-registration after setup | Off | On |
| URLs on private networks in settings | Allowed | Refused |
| Plain `http` in a settings URL | Allowed | Refused, except for a private host when private network URLs are allowed |
| Private code to an AI provider | A workspace can allow it. | Refused until the instance administrator allows it. |

## Prepare the values

1. Make a master key. Keep it in a secret store. Without this key Vellum cannot read the stored credentials.

   <!-- snippet: run -->
   ```sh
   openssl rand -base64 32
   ```

2. Make a setup token. It must have 16 characters or more. This command gives 48.

   <!-- snippet: run -->
   ```sh
   openssl rand -hex 24
   ```

3. Decide the public host name, for example `vellum.example.com`.
4. Put a reverse proxy that terminates HTTPS in front of Vellum. Vellum listens on plain HTTP on port `8080`. See [Configure the reverse proxy](#configure-the-reverse-proxy).
5. Find the address that Vellum sees for the proxy. That address is the value of `Vellum__TrustedProxies`.

| Variable | Value in the hosted profile |
|---|---|
| `Vellum__Profile` | `hosted` |
| `ConnectionStrings__DefaultConnection` | The Postgres connection string. Required. |
| `Vellum__MasterKey` or `Vellum__MasterKeyFile` | Required. The base64 form of 32 random bytes, or a file that holds that value. Set one of them. |
| `Vellum__SetupToken` | Required while no user exists. At least 16 characters. |
| `AllowedHosts` | Required. The public host names, separated by `;`. No `*`, no name that contains `*`, no `0.0.0.0`, `::` or `[::]`, no empty entry. |
| `Vellum__TrustedProxies` | The addresses or the CIDR ranges of your reverse proxies, separated by `;`. Vellum reads the scheme and the address of the client from these senders only. |
| `Vellum__SecureCookies` | Leave it out. `false` is refused. |
| `Database__MigrateOnStartup` | Default `true`. Set `false` only when a separate step applies the migrations. |

[Bootstrap environment](/reference/bootstrap-environment/) lists each variable.

## Configure the reverse proxy

Let nothing but the proxy reach port `8080` of Vellum.

1. Make the proxy pass the `Host` header on unchanged. The value must be one of the names in `AllowedHosts`.
2. Make the proxy set `X-Forwarded-Proto` to the scheme that the browser used.
3. Make the proxy set `X-Forwarded-For` to the address of the client, or append that address to the header.
4. Set `Vellum__TrustedProxies` to the address that Vellum sees for the proxy, for example `10.0.0.5`. For more than one proxy, separate the entries with `;`. An entry can be a CIDR range, for example `172.20.0.0/16`. Name the narrowest range that holds your proxy.

Vellum reads the two headers only from a sender that `Vellum__TrustedProxies` names. It ignores them from each other sender. So a client cannot select the address that the rate limits count, and a client cannot claim HTTPS.

Vellum reads only the last hop of `X-Forwarded-For`. That is the value that your proxy wrote.

Without `Vellum__TrustedProxies`, Vellum ignores the two headers. All clients of the proxy then share one rate limit, and Vellum sends no `Strict-Transport-Security` header.

Vellum refuses to start when an entry is no address and no range, or when a range holds every address.

Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`. Vellum does not need it. The web framework reads it and then trusts the two headers from every sender.

### Caddy

Caddy passes `Host` on unchanged and sets `X-Forwarded-For` and `X-Forwarded-Proto` by default. This `Caddyfile` gets a certificate for the host name and sends each request to the `vellum` service of the Compose example:

<!-- snippet: syntax -->
```text
vellum.example.com {
	reverse_proxy vellum:8080
}
```

When Caddy runs in the same Compose project, set `VELLUM_TRUSTED_PROXIES` to the address of the Caddy container, or to the range of the Compose network.

## Start with the hosted Compose example

The repository has the file `docker-compose.hosted.example.yml`. It starts Vellum in the hosted profile with a Postgres database. It publishes the app port on `127.0.0.1` only and does not publish the database port. The container has a read-only root file system, runs as a user that is not root and has no Linux capability.

1. Copy the file.
2. Write the required values to an env file, for example `hosted.env`. Compose stops with a message when one of them is missing.

   | Variable | Value |
   |---|---|
   | `VELLUM_MASTER_KEY` | The master key. Passed to Vellum as `Vellum__MasterKey`. |
   | `VELLUM_SETUP_TOKEN` | The setup token. Passed as `Vellum__SetupToken`. |
   | `VELLUM_HOSTS` | The public host names, separated by `;`. Passed as `AllowedHosts`. |
   | `VELLUM_POSTGRES_PASSWORD` | The password of the database user. |
   | `VELLUM_TRUSTED_PROXIES` | The addresses or the CIDR ranges of your reverse proxies, separated by `;`. Passed as `Vellum__TrustedProxies`. |

3. Set the optional values that you need.

   | Variable | Default | Value |
   |---|---|---|
   | `VELLUM_BIND` | `127.0.0.1` | The address on the machine that publishes the app port. |
   | `VELLUM_PORT` | `8080` | The app port on the machine. |
   | `VELLUM_SUBNET` | `172.30.250.0/24` | The address range of the Compose network. |
   | `VELLUM_PREVIOUS_MASTER_KEY` | empty | The old key during a rotation. |
   | `VELLUM_PUBLIC_URL` | empty | The https address that people use, for example `https://vellum.example.com`. Links in webhook events start with it. Empty means that Vellum uses the host name of the request. |
   | `VELLUM_VERSION` | `latest` | The release to run. |
   | `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | The address of an OTLP collector. Empty means that telemetry is off. |

4. Start the services.

   ```sh
   docker compose -f docker-compose.hosted.example.yml --env-file hosted.env up -d
   ```

The file gives the Compose network a fixed address range, so that the value of `VELLUM_TRUSTED_PROXIES` stays correct after a restart.

- For a proxy that runs on the Docker host, Vellum sees the gateway of the network. With the default range the gateway is `172.30.250.1`. Set `VELLUM_TRUSTED_PROXIES=172.30.250.1`.
- For a proxy that is a service of the same Compose project, Vellum sees an address in `172.30.250.0/24`.

If Docker answers `Pool overlaps with other one on this address space`, another network uses the range. Set `VELLUM_SUBNET` to a free range. The gateway is the first address of that range. Name it in `VELLUM_TRUSTED_PROXIES`.

The file passes only the variables that it lists. To set another variable of Vellum, add it to the `environment` of the `vellum` service.

## Start one instance with docker run

```sh
docker run -d --name vellum \
  -p 127.0.0.1:8080:8080 \
  -e Vellum__Profile=hosted \
  -e "ConnectionStrings__DefaultConnection=Host=<db-host>;Database=vellum;Username=<db-user>;Password=<db-password>" \
  -e Vellum__MasterKeyFile=/run/secrets/vellum-master-key \
  -e "Vellum__SetupToken=<setup-token>" \
  -e AllowedHosts=vellum.example.com \
  -e "Vellum__TrustedProxies=<proxy-address>" \
  -e Vellum__PublicUrl=https://vellum.example.com \
  -v /srv/vellum/master.key:/run/secrets/vellum-master-key:ro \
  ghcr.io/alternayte/vellum:<version>
```

`<version>` is a release, for example `0.3.0`. The app user in the container must be able to read the key file. `<proxy-address>` is the address that Vellum sees for your reverse proxy.

The image runs as a user that is not root. `docker run` does not add the other limits of the Compose files. To add them, use the options `--read-only`, `--tmpfs /tmp`, `--cap-drop ALL` and `--security-opt no-new-privileges:true`.

## Complete the setup

1. Read the log: `docker compose logs vellum`. A refused start gives one line that starts with `Vellum did not start.` and states the fix. See [Vellum does not start](/troubleshooting/start-up-refusals/).
2. Open `https://vellum.example.com`.
3. Enter the setup token in the setup screen and create the first account. That account is the instance administrator.
4. Remove `Vellum__SetupToken` from the environment after setup. Vellum needs it only while no user exists.
5. Decide whether people can register. In the hosted profile registration is open after setup. The instance administrator changes this with **Let people register themselves** in the instance settings: see [Configure settings](/how-to/configure-settings/).

## Configure sign-in with GitHub

This step is optional. Without it, people sign in with an email address and a password.

1. At GitHub, create an OAuth app.
2. Set the callback URL of the OAuth app to `https://vellum.example.com/api/auth/external/callback`. Use your public host name.
3. Set `Authentication__GitHub__ClientId` and `Authentication__GitHub__ClientSecret` to the client ID and the client secret of the OAuth app.
4. Start Vellum again.

Vellum asks GitHub for the scope `user:email`. It reads the account ID, the display name and the email addresses of the account. It uses the access token for these reads and does not store it.

Vellum accepts only the primary email address of the account, and only when GitHub verified it. An account with no such address gets `400` with the title `The provider gave no verified email address`.

The path `/api/auth/external/callback` is the only path that you register at GitHub. After Vellum checks the answer of GitHub, it sends the browser to `/api/auth/external/complete`. That endpoint creates the account or the session.

A first sign-in with GitHub creates an account, if registration is open. Vellum does not link GitHub to an account that exists. A sign-in with the email address of such an account gets `409`.

On an instance with no client ID, `GET /api/auth/external/GitHub` answers `404`. The sign-in screen shows the button on each instance.

[Setup and sign-in problems](/troubleshooting/setup-and-sign-in/#sign-in-with-github-or-google-fails-in-another-way) lists the answers of a sign-in that fails.

## Run more than one instance

Each instance must have the same database, the same master key and the same `AllowedHosts`.

What the instances share through the database:

- The keys that sign the session cookie. They are in Postgres, sealed with the master key. A session from one instance is valid on each other instance.
- The migrations. Each instance takes a Postgres advisory lock before it applies migrations, so instances that start together do not collide.
- The repository scans. Each instance runs a scan worker. A worker claims one scan with `FOR UPDATE SKIP LOCKED`, so two workers never take the same scan. The worker holds a lease of 60 seconds and renews it while it runs. When an instance stops, the lease ends and another worker takes the scan again. Each claim raises a token, and a write with an old token is refused. A scan whose worker stopped two times in a row is not run a third time.

What the instances do not share:

- The rate limits. The limits for a caller with no credential and the limits per caller count in the memory of one instance. With three instances a client can get three times the limit. [Security model](/concepts/security-model/#limits-on-guessing) gives the numbers.
- A stress run. It executes in the request that starts it, on the instance that got the request. It is not a background job.
- The bound on stress runs at the same time. The instance policy **Stress runs at the same time** applies to each instance by itself. With three instances and a value of 4, the deployment can do 12 runs at the same time.

To run the scan worker on some instances only, set `Import__Worker__Enabled=false` on the other instances. `Import__Worker__Parallelism` sets the number of scans that one instance runs at the same time, from 1 to 32.

The local profile with a generated `master.key` does not fit more than one machine, because the key file is on one volume. Give the key through the environment.

## Health

Vellum has three health endpoints. Each needs no sign-in and gives no detail.

| Endpoint | Answers `200` when | Use it for |
|---|---|---|
| `GET /health` | The app reaches its database. | A load balancer that has one check, and the container health check. |
| `GET /health/ready` | The app reaches its database. | The readiness probe of an orchestrator. |
| `GET /health/live` | The process answers. It does not ask the database. | The liveness probe of an orchestrator. |

`/health` and `/health/ready` answer `503` when the app does not reach its database.

Do not use `/health` or `/health/ready` as a liveness probe. An orchestrator restarts a container when its liveness probe fails. A restart does not repair a database outage.

The container health check runs `dotnet Vellum.dll health`, which calls `/health`.

## Telemetry

Telemetry is off by default. To send traces, metrics and logs to an OTLP collector, set `OTEL_EXPORTER_OTLP_ENDPOINT` and start again. See [Diagnostics and logs](/runbooks/diagnostics-and-logs/#telemetry).

## See also

- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Setup and sign-in problems](/troubleshooting/setup-and-sign-in/)
- [Rotate the master key](/how-to/rotate-the-master-key/)
- [Security model](/concepts/security-model/)
