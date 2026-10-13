---
title: Back up and restore
description: Back up the database and the master key of a Compose deployment, and restore both on empty volumes.
---

This runbook is for the operator of a Docker Compose deployment. It gives the commands to back up a Vellum instance and to restore it.

Run every command in the directory that holds `docker-compose.yml`.

## What a backup holds

A Vellum instance has two parts that you cannot create again.

| Part | Where it is | What it holds |
|---|---|---|
| The database | The `pgdata` volume of the `postgres` service | Accounts, workspaces, models, scenarios, promises, settings, the audit log, and the stored credentials in encrypted form. |
| The master key | The file `/data/master.key` on the `vellum-data` volume, or the value `VELLUM_MASTER_KEY` | The key that decrypts the stored credentials and the keys that sign session cookies. |

A database backup without the master key restores the models. It does not restore the stored credentials or the sessions. Vellum does not start on a restored database when it does not have the key. See [Lost master key](/runbooks/lost-master-key/).

The data directory holds no other data that you must keep. The file `setup-code` exists only before first-run setup is complete.

Keep the copy of the master key apart from the database backup. A person who has both can read every stored credential.

## Back up

Vellum can run during the backup. `pg_dump` reads one consistent state of the database.

1. Write the database to a file.

   <!-- snippet: live -->
   ```sh
   mkdir -p backup
   docker compose exec -T postgres pg_dump -U vellum -d vellum --format=custom > backup/vellum.dump
   ```

2. Copy the master key.

   <!-- snippet: live -->
   ```sh
   docker compose cp vellum:/data/master.key backup/master.key
   chmod 600 backup/master.key
   ```

   If you set `VELLUM_MASTER_KEY`, Vellum does not use the file. Keep a copy of that value in place of the file.

3. Check the two files. The first command lists the tables in the dump. The second command prints `32`: the key is the base64 form of 32 bytes.

   <!-- snippet: live -->
   ```sh
   docker compose exec -T postgres pg_restore --list < backup/vellum.dump | grep -c "TABLE DATA"
   base64 -d < backup/master.key | wc -c
   ```

4. Move `backup/vellum.dump` and `backup/master.key` off the machine, to two different places.

Make a new backup of the master key after each [master key rotation](/how-to/rotate-the-master-key/). The master key does not change at any other time.

## Restore

The restore needs a `postgres` volume with an empty `vellum` database and a `vellum-data` volume with no `master.key`. A new machine with the same `docker-compose.yml` has both.

Restore the database and the key before you start Vellum. If Vellum starts first, it creates a new master key, and it then refuses to start on the restored database.

1. On a machine that still has the old volumes, stop the deployment and remove the volumes. This deletes the current database and the current master key. Do this only after you checked the backup files.

   <!-- snippet: live -->
   ```sh
   docker compose down -v
   ```

2. Start the database only. `--wait` returns when the database accepts connections.

   <!-- snippet: live -->
   ```sh
   docker compose up -d --wait postgres
   ```

3. Load the dump.

   <!-- snippet: live -->
   ```sh
   docker compose exec -T postgres pg_restore -U vellum -d vellum --no-owner --exit-on-error < backup/vellum.dump
   ```

4. Put the master key on the data volume. The command writes the file as the user that runs Vellum, with read access for that user only.

   <!-- snippet: live -->
   ```sh
   docker compose run --rm --no-deps -T --entrypoint sh vellum -c 'umask 077 && cat > /data/master.key' < backup/master.key
   ```

   If the deployment used `VELLUM_MASTER_KEY`, set that value again and do not do this step.

5. Start Vellum. `--wait` returns when the health check reports `healthy`.

   <!-- snippet: live -->
   ```sh
   docker compose up -d --wait
   ```

6. Check the result. The first command prints `200`. The second command must print no line: Vellum writes a line that starts with `VELLUM SETUP CODE` only when the database has no account.

   <!-- snippet: live -->
   ```sh
   curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:${VELLUM_PORT:-8080}/health"
   docker compose logs vellum | grep "VELLUM SETUP CODE" || true
   ```

7. Sign in with an account that existed before the backup. Open a project.
8. Open **Settings**, then **Instance**, then **Diagnostics**. The list of master keys holds one key, and no stored credential is under a key that is not loaded.

A session that was open at the time of the backup is still valid after the restore, because the session keys are in the database.

## What a restore does not bring back

- Changes that people made after the backup.
- A credential that was stored after the backup. Enter it again in the workspace settings.
- A scan, a stress run or an assistant request that was in progress at the time of the backup. Start it again.

## If the restore fails

| Symptom | Cause | Fix |
|---|---|---|
| `pg_restore` stops with `already exists` | The database is not empty: Vellum started on it before the restore. | Do step 1 again, then continue from step 2. |
| Vellum stops at start-up and names a master key that is not loaded | The key on the data volume is not the key of the backup. | Put the correct `master.key` on the volume (step 4). See [Vellum does not start](/troubleshooting/start-up-refusals/). |
| Vellum stops at start-up and says that it found no `master.key` | Step 4 was not done. | Do step 4, then step 5. |
| The key is gone | No copy of the master key exists. | Follow [Lost master key](/runbooks/lost-master-key/). The models survive. The stored credentials do not. |

## Without Compose

Use the same two parts with your own tools.

- Database: `pg_dump --format=custom` against the database of `ConnectionStrings__DefaultConnection`, and `pg_restore` into an empty database.
- Master key: the value of `Vellum__MasterKey`, the file that `Vellum__MasterKeyFile` names, or `master.key` in `Vellum__DataDirectory`. [Bootstrap environment](/reference/bootstrap-environment/) lists these values.

## See also

- [Back up and restore a project](/how-to/back-up-and-restore/)
- [Upgrade](/runbooks/upgrade/)
- [Rotate the master key](/how-to/rotate-the-master-key/)
