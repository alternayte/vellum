---
title: Upgrade Vellum
description: Move a self-hosted instance to newer source with a backup first, and check the result.
---

This guide shows you how to upgrade a self-hosted Vellum that runs with Docker Compose.

Vellum has no tagged release yet. An upgrade moves the instance to a newer commit of the repository. The data format can change between commits until the first release.

## Before you start

- You operate the instance: you have the directory with the Vellum source and `docker-compose.yml`.
- You can sign in as the instance administrator.

## Upgrade

1. Make a backup of the database and the master key. Follow [Back up](/runbooks/backup-and-restore/#back-up). The upgrade changes the database, and no step of Vellum changes it back.
2. Get the new source.

   ```sh
   git pull
   ```

3. Build the image and start it.

   ```sh
   docker compose up -d --build --wait
   ```

   Vellum applies the database migrations when it starts. The command returns when the app is healthy.

4. Sign in. Open **Settings**, then **Instance**, then **Diagnostics**. Check that no pending migration is listed.
5. Open a project and play a scenario.

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

Replace the container image or the published program with the new build, and start it with the same environment. The migrations run at start-up unless `Database__MigrateOnStartup` is `false`. See [Upgrade](/runbooks/upgrade/) in the runbooks.
