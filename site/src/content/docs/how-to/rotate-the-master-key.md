---
title: Rotate the master key
description: Change the key that protects stored credentials, without losing them.
---

This guide shows you how to change the master key.

The master key wraps the key of each stored credential and the keys that sign session cookies. If you lose it, you lose the stored credentials and every session.

## Follow this order

1. Create a new key: `openssl rand -base64 32`.
2. Set `Vellum__MasterKey` to the new key. Set `Vellum__PreviousMasterKey` to the old key.
3. Restart Vellum. It encrypts with the new key and can still read with the old key.
4. Sign in as the instance administrator. Open **Settings**, then **Instance**, and select **Re-encrypt with the current key**. Vellum reports how many stored keys it changed.
5. Remove `Vellum__PreviousMasterKey` and restart.

If step 4 reports an unavailable key, a stored credential uses a key that is not loaded. Nothing changed. Load that key as the previous key and do step 4 again.

Do not remove the previous key before step 4. Credentials that still use it become unreadable.

## Self-hosted profile

The self-hosted profile creates its key in the data directory as `master.key`. To rotate it, copy the content of that file into `Vellum__PreviousMasterKey`, set `Vellum__MasterKey` to a new key, and follow the steps above.

## Back up the key

Keep a copy of the master key apart from the database backup. A database backup without the key cannot restore credentials or sessions.
