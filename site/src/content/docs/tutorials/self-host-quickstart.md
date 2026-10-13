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

`docker compose ps` shows the `vellum` service as `healthy` when the app reaches its database.

## Get the setup code

The first start creates a one-time setup code. The setup screen asks for it. The code proves that you operate this instance.

```sh
docker compose logs vellum | grep "VELLUM SETUP CODE"
```

The line shows the code, for example `VELLUM SETUP CODE: ABCDE-FGHJK-MNPQR-STUVW`.

The code is also in the file `setup-code` in the data directory:

```sh
docker compose exec vellum cat /data/setup-code
```

Vellum writes the same code to the log on each start until setup is complete.

## Create the administrator

1. Open `http://localhost:8080`. Vellum shows the setup screen, because the instance has no account.
2. Give your name, an email and a password. The password has 10 characters or more, with a digit, an upper-case letter and a lower-case letter.
3. Give the setup code.
4. Give the first workspace a name and complete the setup.

You are now signed in as the instance administrator and as the owner of the workspace.

Vellum deletes the `setup-code` file when setup is complete. The code does not work again.

After setup, nobody can register an account by themselves. To change that, open **Settings**, then **Instance**.

Vellum accepts 10 setup attempts and 10 sign-in attempts in one minute from one address. After that it answers `429` and tells the client how long to wait.

## Who can reach it

Compose publishes the app and the database on `127.0.0.1` only. Other machines on your network cannot connect.

To let other machines reach the app:

1. Put a reverse proxy with HTTPS in front of the app.
2. Set `VELLUM_BIND=0.0.0.0`, or the address of one network interface, before `docker compose up -d`.

`VELLUM_BIND` moves the app port only. The database port stays on `127.0.0.1`, because the Compose database has a well-known password. `VELLUM_POSTGRES_BIND` moves the database port. Change it only on a network that you trust.

## Stop and start

```sh
docker compose stop
docker compose start
```

Your session, your settings and your stored credentials survive a restart, because the database and the master key are on volumes.

`docker compose down -v` deletes both volumes. That deletes the model data and the master key.

Do not delete the `vellum-data` volume by itself. Vellum does not start on a database that holds data under a master key that it does not have. [Rotate the master key](/how-to/rotate-the-master-key/) tells you what to do if the key is lost.

## Next

- [Configure a workspace](/how-to/configure-settings/)
- [Trace a scenario](/how-to/trace-a-scenario/)
