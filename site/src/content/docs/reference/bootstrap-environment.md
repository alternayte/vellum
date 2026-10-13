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
| `Vellum__SetupToken` | none | The token that the first administrator types in the setup screen. At least 16 characters. Required in the hosted profile while no user exists. Optional in the local profile. |
| `Vellum__SecureCookies` | `true` in hosted | Marks the session cookie as Secure. |
| `AllowedHosts` | `*` | The public host names of the instance, separated by `;`. |
| `Database__MigrateOnStartup` | `true` | Applies database migrations when Vellum starts. Set `false` when a separate step applies them. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | Set `true` behind a reverse proxy. Vellum then reads the address of the client from the proxy. The rate limit for sign-in and setup counts by that address. |
| `Authentication__GitHub__ClientId`, `…ClientSecret` | none | Optional sign-in with GitHub. |
| `Authentication__Google__ClientId`, `…ClientSecret` | none | Optional sign-in with Google. |

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

## The two profiles

| | `local` | `hosted` |
|---|---|---|
| Master key | Created in the data directory if you give none | Required |
| First account | The setup screen, with the setup code from the log, or with `Vellum__SetupToken` if you set one | The setup screen, with `Vellum__SetupToken` |
| Self-registration after setup | Off | On |
| URLs on private networks | Allowed | Refused |
| Plain `http` in a settings URL | Allowed | Refused, except for a private host when private network URLs are allowed |
| Sending private code to AI | A workspace can allow it | Refused until the instance administrator allows it |

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
- The database holds stored credentials or session keys under a master key that is not loaded. The message gives the ID of each missing key, the number of rows that use it, and the IDs of the loaded keys. See [Rotate the master key](/how-to/rotate-the-master-key/).

In the local profile:

- No master key is set, no `master.key` file exists, and the database holds data under a key. Vellum does not create a new key.
- No master key is set and the database cannot be reached to make that check.
- Users other than the owner can read or change `master.key`. The fix is `chmod 600` on the file.
- `master.key` is empty, or the data directory cannot be written.
- The `setup-code` file cannot be written.

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
| `dotnet Vellum.dll health` | Calls `/health` on the same machine. It exits with code 0 when the answer is `200`. The container health check runs it. |

## Limits on sign-in and setup

`POST /api/auth/login` and `POST /api/setup` accept 10 calls in one minute from one client address. After that they answer `429` with a `Retry-After` header.

Five wrong passwords lock an account for five minutes. Sign-in answers `401` for a wrong password, for an unknown account and for a locked account.

A new password has 10 characters or more.

A request body that is larger than its limit gets `413` before Vellum reads it: 4 KB for sign-in and setup, 32 KB for a credential, 64 KB for workspace settings and 68 KB for instance settings.

## Sign-in with GitHub or Google

A first sign-in with a provider creates an account, if the instance allows registration. It never creates the first account; only setup does.

If an account with the same email address already exists, Vellum refuses the sign-in with `409`. Vellum does not link a provider to an existing account. Sign in with the password of that account.

## Health

`GET /health` returns `200` when the app and the database are ready, and `503` when they are not. It needs no sign-in and gives no detail. The instance administrator reads details under **Settings → Instance → Diagnostics**.

The container image and the Compose file have a health check. `docker compose ps` shows `healthy` when `/health` returns `200`.
