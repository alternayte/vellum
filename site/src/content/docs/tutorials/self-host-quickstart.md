---
title: Run Vellum on your machine
description: Start Vellum with Docker Compose and create the first account.
---

In this tutorial you start Vellum and Postgres with one command, then create the administrator and a workspace.

You need Docker with the Compose plugin.

## Start it

Make a directory and get the Compose file of the latest release:

<!-- snippet: live -->
```sh
mkdir vellum && cd vellum
curl -fsSLO https://github.com/alternayte/vellum/releases/latest/download/docker-compose.yml
```

Start the app and the database:

<!-- snippet: live -->
```sh
docker compose up -d --wait
```

The first start downloads the image `ghcr.io/alternayte/vellum`. `--wait` returns when both services are ready. To run one version, set `VELLUM_VERSION`, for example `VELLUM_VERSION=0.1.0`. With no value, Compose runs `latest`.

Vellum listens on `http://localhost:8080`. To use other ports, set `VELLUM_PORT` and `VELLUM_POSTGRES_PORT` before the command.

Vellum creates its database tables on the first start. It also creates a master key and keeps it on the `vellum-data` volume. You set no configuration.

`docker compose ps` shows the `vellum` service as `healthy` when the app reaches its database.

The Compose file limits the `vellum` container. The process is not root and has no Linux capability. The root file system is read-only. Vellum writes to the `vellum-data` volume on `/data` and to a temporary directory in memory, and to nothing else.

Vellum sends no telemetry to another machine. [Diagnostics and logs](/runbooks/diagnostics-and-logs/#telemetry) shows how to turn it on.

## Get the setup code

The first start creates a one-time setup code. The setup screen asks for it. The code proves that you operate this instance.

<!-- snippet: live -->
```sh
docker compose logs vellum | grep "VELLUM SETUP CODE"
```

The line shows the code, for example `VELLUM SETUP CODE: ABCDE-FGHJK-MNPQR-STUVW`.

The code is also in the file `setup-code` in the data directory:

<!-- snippet: live -->
```sh
docker compose exec vellum cat /data/setup-code
```

Vellum writes the same code to the log on each start until setup is complete.

## Create the administrator

1. Open `http://localhost:8080/setup`. An instance that has no account needs setup.
2. Fill in **Your name**, **Email** and **Password**. The password has 10 characters or more, with a digit, an upper-case letter and a lower-case letter.
3. Select **Continue**.
4. Fill in **Workspace name**.
5. Type the setup code in **Setup code**.
6. Select **Finish setup**.

You are now signed in as the instance administrator and as the owner of the workspace.

Vellum deletes the `setup-code` file when setup is complete. The code does not work again.

After setup, nobody can register an account by themselves. To change that, select **Settings** on the home screen, then **Instance**, and turn on **Let people register themselves**.

If the code is wrong, Vellum answers `403` with the error `invalid_setup_token`. A second setup on the same instance gets `409` with the error `already_set_up`.

Vellum accepts 10 setup attempts and 10 sign-in attempts in one minute from one address. After that it answers `429` with the error `rate_limited`. The `Retry-After` header gives the number of seconds to wait.

Five wrong passwords lock an account for 5 minutes.

## Who can reach it

Compose publishes the app and the database on `127.0.0.1` only. Other machines on your network cannot connect.

To let other machines reach the app:

1. Put a reverse proxy with HTTPS in front of the app.
2. Set `VELLUM_BIND=0.0.0.0`, or the address of one network interface, before `docker compose up -d`.

`VELLUM_BIND` moves the app port only. The database port stays on `127.0.0.1`, because the Compose database has a well-known password. `VELLUM_POSTGRES_BIND` moves the database port. Change it only on a network that you trust.

## Stop and start

<!-- snippet: live -->
```sh
docker compose stop
docker compose start
```

Your session, your settings and your stored credentials survive a restart, because the database and the master key are on volumes.

`docker compose down -v` deletes both volumes. That deletes the model data and the master key.

Do not delete the `vellum-data` volume by itself. Vellum does not start on a database that holds data under a master key that it does not have. [Lost master key](/runbooks/lost-master-key/) tells you what to do if the key is lost.

## Next

- [Build a first model and trace it](/tutorials/first-model-and-trace/)
- [Configure a workspace](/how-to/configure-settings/)
- [Back up and restore](/how-to/back-up-and-restore/)
- [Bootstrap environment](/reference/bootstrap-environment/)
- [Start-up refusals](/troubleshooting/start-up-refusals/)
