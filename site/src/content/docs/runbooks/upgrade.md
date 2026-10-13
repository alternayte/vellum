---
title: Upgrade
description: Move a Compose deployment to a newer version of Vellum, check the result and go back if it fails.
---

This runbook is for the operator of a Docker Compose deployment. Run every command in the directory of the Vellum source, which holds `docker-compose.yml`.

Vellum has no tagged release yet. A version is a commit of the repository. The data format can change between commits until the first release.

## What an upgrade does

- You build a new image from newer source.
- When the new image starts, Vellum applies the database migrations of every module before it accepts requests. The value `Database__MigrateOnStartup` controls this. Its default is `true`.
- Migrations add tables and columns. They do not remove data. Vellum has no migration that goes back to an older version.
- When more than one instance starts at the same time, one instance applies the migrations and the others wait.

Because no migration goes back, the backup that you make before the upgrade is the only way back.

## Before the upgrade

1. Make a backup: [Back up and restore](/runbooks/backup-and-restore/#back-up). Do not continue without the dump and the master key.
2. Write down the commit that runs now.

   <!-- snippet: syntax -->
   ```sh
   git rev-parse HEAD
   ```

3. Read the changes between that commit and the new one. Look for a new value in [Bootstrap environment](/reference/bootstrap-environment/) and for a note about the data format.

## Upgrade

1. Get the new source.

   <!-- snippet: syntax -->
   ```sh
   git pull
   ```

2. Build the image and replace the container. `--wait` returns when the health check reports `healthy`. The database container and both volumes stay.

   <!-- snippet: live -->
   ```sh
   docker compose up -d --build --wait
   ```

   People who use the app get errors while the container restarts. Their sessions stay valid.

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

3. Sign in as the instance administrator. Open **Settings**, then **Instance**, then **Diagnostics**. No module lists a pending migration, and the database and the event store are reachable. `GET /api/instance/diagnostics` gives the same data.
4. Open a project and play one scenario.

## If the upgrade fails

A failed migration stops Vellum at start-up. The container exits, and the log ends with the error of the migration.

1. Keep the log.

   <!-- snippet: syntax -->
   ```sh
   docker compose logs vellum > upgrade-failure.log
   ```

2. Go back to the commit that you wrote down.

   <!-- snippet: syntax -->
   ```sh
   git checkout <commit>
   ```

3. Restore the backup on empty volumes, with the old source: [Back up and restore](/runbooks/backup-and-restore/#restore). Do not start the old version on a database that the new version migrated.

Changes that people made after the backup are lost in this case. For that reason, make the backup immediately before the upgrade.

## Migrations as a separate step

Set `Database__MigrateOnStartup=false` when a deployment step applies the migrations and the app must not.

Vellum then starts only on a database that is up to date. On a database that is behind, the event store stops the start with the error `DBX001`. [Vellum does not start](/troubleshooting/start-up-refusals/) describes it.

The `justfile` has one recipe for each module to create a migration. The repository has no command that only applies migrations: a start with `Database__MigrateOnStartup=true` applies them.

## See also

- [Upgrade Vellum](/how-to/upgrade/)
- [Diagnostics and logs](/runbooks/diagnostics-and-logs/)
