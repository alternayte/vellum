---
title: Run Vellum on your machine
description: Start Vellum with Docker Compose and create the first account.
---

In this tutorial you start Vellum and Postgres with one command, then create the administrator and a workspace.

You need Docker with the Compose plugin.

## Start it

```sh
git clone https://github.com/alternayte/vellum
cd vellum
docker compose up -d
```

Vellum listens on `http://localhost:8080`. To use other ports, set `VELLUM_PORT` and `VELLUM_POSTGRES_PORT` before the command.

Vellum creates its database tables on the first start. It also creates a master key and keeps it on the `vellum-data` volume. You set no configuration.

## Create the administrator

1. Open `http://localhost:8080`. Vellum shows the setup screen, because the instance has no account.
2. Give your name, an email and a password. Select **Continue**.
3. Give the first workspace a name. Select **Finish setup**.

You are now signed in as the instance administrator and as the owner of the workspace.

After setup, nobody can register an account by themselves. To change that, open **Settings**, then **Instance**.

## Stop and start

```sh
docker compose stop
docker compose start
```

Your session, your settings and your stored credentials survive a restart, because the database and the master key are on volumes.

`docker compose down -v` deletes both volumes. That deletes the model data and the master key.

## Next

- [Configure a workspace](/how-to/configure-settings/)
- [Trace a scenario](/how-to/trace-a-scenario/)
