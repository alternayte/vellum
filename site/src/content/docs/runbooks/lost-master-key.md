---
title: "Incident: lost master key"
description: Start an instance again when its master key is gone, and know what is lost.
---

This runbook is for the operator of an instance that does not start because its master key is gone. Run every command in the directory that holds `docker-compose.yml`.

## Symptom

The `vellum` container stops a short time after it starts. `docker compose ps -a` shows `Exited (1)`.

<!-- snippet: syntax -->
```sh
docker compose logs --tail 20 vellum
```

The log ends with one of these messages.

- No key file, and the database holds encrypted rows:

  ```text
  Vellum did not start. The master key file '/data/master.key' does not exist, and the database holds rows that are encrypted under another master key: key a0d93d08155b7027 (1 stored credential, 1 session key). Vellum does not create a new key, because a new key cannot read these rows. Restore the key file from a backup, or set Vellum__MasterKey to the key that was used. If the key is lost and you accept the loss of these rows, run 'dotnet Vellum.dll keys forget --yes' and start again.
  ```

- A key is loaded, and it is not the key of the stored rows. The message names the ID of each missing key, the number of rows that use it, and the IDs of the loaded keys.

The ID of a key is not the key. You cannot get the key back from its ID.

## Decide: restore or forget

| You have | Do this |
|---|---|
| A copy of `master.key`, or of the value of `VELLUM_MASTER_KEY` | Restore it. Nothing is lost. |
| No copy | Forget the rows that use the lost key. The models survive. The stored credentials and the sessions do not. |

Look for a copy before you decide: the backup of the data volume, a secret manager, the shell history or the `.env` file of the person who rotated the key.

## Restore the key

1. Put the file back on the data volume.

   <!-- snippet: syntax -->
   ```sh
   docker compose run --rm --no-deps -T --entrypoint sh vellum -c 'umask 077 && cat > /data/master.key' < backup/master.key
   ```

   If the deployment used `VELLUM_MASTER_KEY`, set that value in place of this step.

2. Start Vellum.

   <!-- snippet: syntax -->
   ```sh
   docker compose up -d --wait
   ```

## Forget the rows that use the lost key

What this deletes:

- Every stored credential that uses the lost key: AI provider keys, the GitHub token and the webhook signing secret of every workspace.
- Every session key that uses the lost key. Every person must sign in again.

What stays: accounts and passwords, workspaces and members, models, scenarios, promises, evidence, settings and the audit log.

1. List the rows. The command deletes nothing. It exits with code 1 when it lists a row.

   <!-- snippet: syntax -->
   ```sh
   docker compose run --rm --no-deps vellum keys forget
   ```

   ```text
   The environment gives no master key.
   Rows under keys that the environment does not give: key a0d93d08155b7027 (1 stored credential, 1 session key).
   Nothing was deleted. These rows cannot be read again after they are deleted. Every user must sign in again and every listed credential must be entered again. Run the command with --yes to delete them.
   ```

2. Delete the rows.

   <!-- snippet: syntax -->
   ```sh
   docker compose run --rm --no-deps vellum keys forget --yes
   ```

   The command ends with `The rows were deleted. Start Vellum again.` It records the deletion in the audit log.

3. Start Vellum. In the local profile with no `VELLUM_MASTER_KEY`, Vellum now creates a new `master.key`.

   <!-- snippet: syntax -->
   ```sh
   docker compose up -d --wait
   ```

4. Back up the new key now. See [Back up and restore](/runbooks/backup-and-restore/#back-up).
5. Sign in. Open **Settings**, then **Instance**, then **Diagnostics**. The list of master keys holds the new key only.
6. Tell each workspace owner to enter the credentials of the workspace again. See [Configure a workspace](/how-to/configure-settings/#store-a-credential).

Vellum never deletes these rows by itself. Without Compose, the command is `dotnet Vellum.dll keys forget`, with the same environment as the app.

## After the incident

- Find out how the key was lost. The usual causes are a removed `vellum-data` volume and a database restore with no key backup.
- Keep one copy of the master key apart from the database backup.
- Treat each credential that was stored in Vellum as still valid at its provider. A lost key does not expose a credential, and it does not revoke one.

## See also

- [Rotate the master key](/how-to/rotate-the-master-key/)
- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Bootstrap environment](/reference/bootstrap-environment/)
