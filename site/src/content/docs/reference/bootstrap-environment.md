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
| `Vellum__DataDirectory` | `./data` | Local profile only: where Vellum keeps the master key it creates. |
| `Vellum__SetupToken` | none | Hosted profile: the one-time token the first administrator types in the setup screen. At least 16 characters. |
| `Vellum__SecureCookies` | `true` in hosted | Marks the session cookie as Secure. |
| `AllowedHosts` | `*` | The public host names of the instance, separated by `;`. |
| `Database__MigrateOnStartup` | `true` | Applies database migrations when Vellum starts. Set `false` when a separate step applies them. |
| `Authentication__GitHub__ClientId`, `…ClientSecret` | none | Optional sign-in with GitHub. |
| `Authentication__Google__ClientId`, `…ClientSecret` | none | Optional sign-in with Google. |

## The two profiles

| | `local` | `hosted` |
|---|---|---|
| Master key | Created in the data directory if you give none | Required |
| First account | The setup screen, no token | The setup screen, with `Vellum__SetupToken` |
| Self-registration after setup | Off | On |
| URLs on private networks | Allowed | Refused |
| Sending private code to AI | A workspace can allow it | Refused until the instance administrator allows it |

## When the hosted profile refuses to start

The hosted profile stops at start-up, with a message that states the fix, when:

- No master key is set.
- `AllowedHosts` is `*`.
- `Vellum__SecureCookies` is `false`.
- No user exists and `Vellum__SetupToken` is missing or shorter than 16 characters.

## Health

`GET /health` returns `200` when the app and the database are ready, and `503` when they are not. It needs no sign-in and gives no detail. The instance administrator reads details under **Settings → Instance → Diagnostics**.
