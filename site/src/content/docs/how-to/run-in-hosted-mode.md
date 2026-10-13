---
title: Run Vellum in hosted mode
description: Start Vellum with the hosted profile, with the environment it requires, behind HTTPS and with more than one instance.
---

This guide shows you how to start Vellum with the hosted profile. The hosted profile is for an instance that more than one team reaches over a public address.

## Know the status first

The hosted profile exists in the code. It is not ready for use.

- The automated tests exercise the hosted profile. Nobody has run the hosted profile in a container.
- The isolation between the workspaces of different tenants has no test.
- Sign-in with GitHub is not tested and is expected to fail. The sign-in registration uses the same path as the callback route of the app, and it maps no email address. The callback route refuses a sign-in without an email address with `400` and `Email not provided by external provider`.

Use the local profile for real work. [Local and hosted profiles](/concepts/local-and-hosted-profiles/) explains the difference.

## Know what the profile changes

| | `local` | `hosted` |
|---|---|---|
| Master key | Vellum creates `master.key` in the data directory if you give none. | You must give the key. |
| Session cookie | Secure only when the request came over HTTPS. | Always Secure. `Vellum__SecureCookies=false` is refused. |
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

2. Make a setup token of 16 characters or more.

   <!-- snippet: run -->
   ```sh
   openssl rand -hex 16
   ```

3. Decide the public host name, for example `vellum.example.com`.
4. Put a reverse proxy that terminates HTTPS in front of Vellum. Vellum listens on plain HTTP on port `8080`.

| Variable | Value in the hosted profile |
|---|---|
| `Vellum__Profile` | `hosted` |
| `ConnectionStrings__DefaultConnection` | The Postgres connection string. Required. |
| `Vellum__MasterKey` or `Vellum__MasterKeyFile` | Required. The base64 form of 32 random bytes, or a file that holds that value. Set one of them. |
| `Vellum__SetupToken` | Required while no user exists. At least 16 characters. |
| `AllowedHosts` | Required. The public host names, separated by `;`. No `*`, no name that contains `*`, no `0.0.0.0`, `::` or `[::]`, no empty entry. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind the reverse proxy. Vellum then reads the scheme and the address of the client from the proxy. |
| `Vellum__SecureCookies` | Leave it out. `false` is refused. |
| `Database__MigrateOnStartup` | Default `true`. Set `false` only when a separate step applies the migrations. |

[Bootstrap environment](/reference/bootstrap-environment/) lists each variable.

## Start one instance with docker run

```sh
docker run -d --name vellum \
  -p 127.0.0.1:8080:8080 \
  -e Vellum__Profile=hosted \
  -e "ConnectionStrings__DefaultConnection=Host=<db-host>;Database=vellum;Username=<db-user>;Password=<db-password>" \
  -e Vellum__MasterKeyFile=/run/secrets/vellum-master-key \
  -e "Vellum__SetupToken=<setup-token>" \
  -e AllowedHosts=vellum.example.com \
  -e ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  -v /srv/vellum/master.key:/run/secrets/vellum-master-key:ro \
  "<vellum-image>"
```

`<vellum-image>` is the image that you build from the `Dockerfile` of the repository. The app user in the container must be able to read the key file.

## Start with a Compose override

The Compose file of the repository sets `Vellum__Profile` to `local`. Put the hosted values in an override file.

```yaml
services:
  vellum:
    environment:
      Vellum__Profile: hosted
      AllowedHosts: vellum.example.com
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
```

Save it as `docker-compose.hosted.yml`. The Compose file passes `VELLUM_MASTER_KEY` to Vellum as `Vellum__MasterKey` and `VELLUM_SETUP_TOKEN` as `Vellum__SetupToken`.

```sh
export VELLUM_MASTER_KEY="<master-key>"
export VELLUM_SETUP_TOKEN="<setup-token>"
docker compose -f docker-compose.yml -f docker-compose.hosted.yml up -d
```

The database of the Compose file has a well-known password. Use your own Postgres for an instance that other people reach, and set `ConnectionStrings__DefaultConnection` in the override.

## Complete the setup

1. Read the log: `docker compose logs vellum`. A refused start gives one line that starts with `Vellum did not start.` and states the fix. See [Vellum does not start](/troubleshooting/start-up-refusals/).
2. Open `https://vellum.example.com`.
3. Enter the setup token in the setup screen and create the first account. That account is the instance administrator.
4. Remove `Vellum__SetupToken` from the environment after setup. Vellum needs it only while no user exists.
5. Decide whether people can register. In the hosted profile registration is open after setup. The instance administrator changes this with **Let people register themselves** in the instance settings: see [Configure settings](/how-to/configure-settings/).

## Run more than one instance

Each instance must have the same database, the same master key and the same `AllowedHosts`.

What the instances share through the database:

- The keys that sign the session cookie. They are in Postgres, sealed with the master key. A session from one instance is valid on each other instance.
- The migrations. Each instance takes a Postgres advisory lock before it applies migrations, so instances that start together do not collide.
- The repository scans. Each instance runs a scan worker. A worker claims one scan with `FOR UPDATE SKIP LOCKED`, so two workers never take the same scan. The worker holds a lease of 60 seconds and renews it while it runs. When an instance stops, the lease ends and another worker takes the scan again. Each claim raises a token, and a write with an old token is refused. A scan whose worker stopped two times in a row is not run a third time.

What the instances do not share:

- The rate limits. The limit for sign-in and setup (10 calls in one minute from one address) and the limits per caller count in the memory of one instance. With three instances a client can get three times the limit.
- A stress run. It executes in the request that starts it, on the instance that got the request. It is not a background job.

To run the scan worker on some instances only, set `Import__Worker__Enabled=false` on the other instances. `Import__Worker__Parallelism` sets the number of scans that one instance runs at the same time, from 1 to 32.

The local profile with a generated `master.key` does not fit more than one machine, because the key file is on one volume. Give the key through the environment.

## Health

`GET /health` returns `200` when the app and the database are ready and `503` when they are not. Point the load balancer at it. The container health check runs `dotnet Vellum.dll health`.

## See also

- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Setup and sign-in problems](/troubleshooting/setup-and-sign-in/)
- [Rotate the master key](/how-to/rotate-the-master-key/)
- [Security model](/concepts/security-model/)
