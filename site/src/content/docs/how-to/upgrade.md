---
title: Upgrade Vellum
description: Move a self-hosted instance to a newer release with a backup first, and check the result.
---

This guide shows you how to upgrade a self-hosted Vellum that runs with Docker Compose.

Each release is the image `ghcr.io/alternayte/vellum:<version>`. The Compose file runs the version in `VELLUM_VERSION`, or `latest` when it has no value. A release never changes a migration of an earlier release, so your data moves to each later version.

## Before you start

- You operate the instance: you have the directory with `docker-compose.yml`.
- You can sign in as the instance administrator.

## Upgrade

1. Make a backup of the database and the master key. Follow [Back up](/runbooks/backup-and-restore/#back-up). The upgrade changes the database, and no step of Vellum changes it back.
2. Read the [release notes](https://github.com/alternayte/vellum/releases) of each version after yours.
3. Set the new version in the file `.env` next to `docker-compose.yml`, for example `VELLUM_VERSION=0.2.0`.
4. Get the image and start it.

   ```sh
   docker compose pull vellum && docker compose up -d --wait
   ```

   Vellum applies the database migrations when it starts. The command returns when the app is healthy.

5. Sign in. Open **Settings**, then **Instance**, then **Diagnostics**. Check that no pending migration is listed.
6. Open a project and play a scenario.

Your session, your settings and your stored credentials survive the upgrade.

## After an upgrade

A new version can change an extraction rule. The next scan of a repository then matches the accepted facts to the facts that it finds. When it cannot match a fact exactly, the review asks you to choose. See [Extraction rules](/reference/extraction-rules/).

## If Vellum does not start

Read the log.

```sh
docker compose logs --tail 50 vellum
```

[Vellum does not start](/troubleshooting/start-up-refusals/) explains each message. To go back to the old version, follow [If the upgrade fails](/runbooks/upgrade/#if-the-upgrade-fails).

## Without Compose

Replace the container image with the image of the new release, and start it with the same environment. The migrations run at start-up unless `Database__MigrateOnStartup` is `false`. See [Upgrade](/runbooks/upgrade/) in the runbooks.
