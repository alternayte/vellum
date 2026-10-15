---
title: Upgrade
description: Move a Compose deployment to a newer version of Vellum, check the result and go back if it fails.
---

This runbook is for the operator of a Docker Compose deployment. Run every command in the directory of the Vellum source, which holds `docker-compose.yml`.

Vellum publishes each release as the image `ghcr.io/alternayte/vellum:<version>`, with release notes on [GitHub](https://github.com/alternayte/vellum/releases). The Compose file runs the version in `VELLUM_VERSION`. With no value, it runs `latest`, the newest release.

## What an upgrade does

- The new image replaces the container. The database container and both volumes stay.
- When the new image starts, Vellum applies the database migrations of every module before it accepts requests. The value `Database__MigrateOnStartup` controls this. Its default is `true`.
- A release adds migrations. It never changes a migration of an earlier release, so a database of any release upgrades to a later one.
- Migrations add tables and columns. They do not remove data. Vellum has no migration that goes back to an older version.
- When more than one instance starts at the same time, one instance applies the migrations and the others wait.

Because no migration goes back, the backup that you make before the upgrade is the only way back.

## Before the upgrade

1. Make a backup: [Back up and restore](/runbooks/backup-and-restore/#back-up). Do not continue without the dump and the master key.
2. Write down the version that runs now. `GET /api/instance` gives it as `version`.
3. Read the release notes of each version between that version and the new one. Look for a new value in [Bootstrap environment](/reference/bootstrap-environment/).

## Upgrade

1. Set the new version in the file `.env` next to `docker-compose.yml`, for example:

   <!-- snippet: syntax -->
   ```sh
   VELLUM_VERSION=0.2.0
   ```

2. Get the image and replace the container. `--wait` returns when the health check reports `healthy`.

   <!-- snippet: live -->
   ```sh
   docker compose pull vellum && docker compose up -d --wait
   ```

   People who use the app get errors while the container restarts. Their sessions stay valid.

To run a build from source instead, check out the tag of the release (`git checkout v0.2.0`) and run `docker compose up -d --build --wait`.

## Check the result

1. The health endpoint answers `200`.

   <!-- snippet: live -->
   ```sh
   curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health"
   ```

2. The log has no line that starts with `Vellum did not start`.

   <!-- snippet: live -->
   ```sh
   docker compose logs vellum | grep "Vellum did not start" || true
   ```

3. Sign in as the instance administrator. Open **Settings**, then **Instance**, then **Diagnostics**. No pending migration is listed, and the database and the event store are reachable. `GET /api/instance/diagnostics` gives the same data.
4. Open a project and play one scenario.

## If the upgrade fails

A failed migration stops Vellum at start-up. The container exits, and the log ends with the error of the migration.

1. Keep the log.

   <!-- snippet: syntax -->
   ```sh
   docker compose logs vellum > upgrade-failure.log
   ```

2. Set `VELLUM_VERSION` in `.env` back to the version that you wrote down.
3. Restore the backup on empty volumes, with the old version: [Back up and restore](/runbooks/backup-and-restore/#restore). Do not start the old version on a database that the new version migrated.

Changes that people made after the backup are lost in this case. For that reason, make the backup immediately before the upgrade.

## Migrations as a separate step

Set `Database__MigrateOnStartup=false` when a deployment step applies the migrations and the app must not.

Vellum then starts only on a database that is up to date. On a database that is behind, the event store stops the start with the error `DBX001`. [Vellum does not start](/troubleshooting/start-up-refusals/) describes it.

The `justfile` has one recipe to create a migration (`just migration <name>`). The repository has no command that only applies migrations: a start with `Database__MigrateOnStartup=true` applies them.

## See also

- [Upgrade Vellum](/how-to/upgrade/)
- [Diagnostics and logs](/runbooks/diagnostics-and-logs/)
