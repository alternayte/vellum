---
title: Rotate the master key
description: Change the key that protects stored credentials, without losing them.
---

This guide shows you how to change the master key.

The master key wraps the key of each stored credential and the keys that sign session cookies. If you lose it, you lose the stored credentials and every session.

## Follow this order

1. Create a new key: `openssl rand -base64 32`.
2. Set `Vellum__MasterKey` to the new key. Set `Vellum__PreviousMasterKey` to the old key.
3. Restart every instance of Vellum. Each instance encrypts with the new key and can still read with the old key.
4. Sign in as the instance administrator. Open **Settings**, then **Instance**, and start the key rotation. This calls `POST /api/instance/keys/rotate`.
5. Read the result. `rewrapped` is the number of stored keys that Vellum moved to the new key. `remaining` is the number that are still under another key.
6. If `remaining` is not 0, do not continue. Stop each instance that still uses the old key as `Vellum__MasterKey`. Then do step 4 again.
7. When `remaining` is 0, remove `Vellum__PreviousMasterKey` and restart.

Do not remove the previous key while `remaining` is more than 0. Vellum then refuses to start, because it cannot read the stored keys that still use the old key.

A credential that a person saves during the rotation is safe. The rotation locks each row while it changes it.

The fingerprint of each credential changes in a rotation, because the fingerprint depends on the master key.

**Settings → Instance → Diagnostics** lists each master key that stored data uses, with the number of credentials and session keys. After a complete rotation the list holds the current key only.

## If the rotation reports an unavailable key

The answer `key_unavailable` means that stored data uses a key that is not loaded. Nothing changed. The message gives the ID of that key. Load that key as the previous key and do step 4 again.

## Self-hosted profile

The self-hosted profile creates its key in the data directory as `master.key`. To rotate it:

1. Read the old key: `docker compose exec vellum cat /data/master.key`.
2. Set `VELLUM_PREVIOUS_MASTER_KEY` to the old key and `VELLUM_MASTER_KEY` to a new key. Compose passes them to Vellum as `Vellum__PreviousMasterKey` and `Vellum__MasterKey`.
3. Run `docker compose up -d` and follow the steps above from step 4.
4. Keep `VELLUM_MASTER_KEY` set from now on.

While `Vellum__MasterKey` is set, Vellum does not read `master.key`. Vellum writes a warning to the log on each start while the unused file is there. After the rotation the file holds the old key. Keep it as a backup or delete it.

If you remove `Vellum__MasterKey` later, Vellum reads `master.key` again. It then refuses to start, because the stored data uses the new key.

## When Vellum refuses to start

Vellum compares the keys that it has with the keys that the stored data uses. It does this on each start, in both profiles. If stored data uses a key that is not loaded, Vellum stops. The message gives:

- the ID of each missing key,
- the number of stored credentials and session keys that use it,
- the IDs of the keys that are loaded.

Vellum also stops in these cases:

- The self-hosted profile finds no `master.key`, and the database holds data under a key. Vellum does not create a new key.
- Other users of the machine can read or change `master.key`. Run `chmod 600` on the file.

To fix a missing key, do one of these:

- Start Vellum with the missing key as `Vellum__MasterKey`.
- During a rotation, keep the new key as `Vellum__MasterKey` and set the missing key as `Vellum__PreviousMasterKey`.
- In the self-hosted profile, put the `master.key` file back from a backup.

## If the key is lost

Without the key, the stored credentials and the session keys cannot be read. You can delete them and keep the rest of the instance: accounts, workspaces, models and settings.

1. Run the command with the same environment as the app, and read what it lists:

   ```sh
   docker compose run --rm vellum keys forget
   ```

   Without Compose the command is `dotnet Vellum.dll keys forget`.

2. Run it again with `--yes` to delete the listed rows:

   ```sh
   docker compose run --rm vellum keys forget --yes
   ```

3. Start Vellum.

The command deletes each stored credential and each session key that uses a key that the environment does not give. It records the deletion in the audit log. Vellum never does this by itself.

After the command, every user signs in again and a workspace owner enters each deleted credential again.

## Back up the key

Keep a copy of the master key apart from the database backup. A database backup without the key cannot restore credentials or sessions.
