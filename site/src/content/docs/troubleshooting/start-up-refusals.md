---
title: Vellum does not start
description: Each message that Vellum writes when it refuses to start, its cause and the fix.
---

Vellum checks its environment before it serves a request. When a value is not safe, Vellum writes one line to the container log and stops with exit code 1.

## Read the message

1. Read the log.

   ```sh
   docker compose logs vellum
   ```

2. Find the line that starts with one of these texts.

| Start of the line | Meaning |
|---|---|
| `Vellum did not start.` | A refusal. The text after it states the fix. The entries on this page quote that text. |
| `Vellum stopped on an error.` | An error that is not a refusal, such as a database that cannot be reached. The text after it is the error. |

3. Find the entry below that has the same text.

Vellum never writes the value of a key to the log.

The messages name the variables with two underscores, for example `Vellum__MasterKey`. With the Compose file of the repository you set `VELLUM_MASTER_KEY`, `VELLUM_PREVIOUS_MASTER_KEY` and `VELLUM_SETUP_TOKEN`. Compose passes them to Vellum. See [Bootstrap environment](/reference/bootstrap-environment/).

## The profile is not valid

**Message:** `Vellum__Profile is not valid. Set it to 'local' or 'hosted'.`

**Cause:** `Vellum__Profile` has another value.

**Fix:**

1. Set `Vellum__Profile` to `local` or `hosted`, or remove it. The default is `local`.
2. Start again.

## The master key is not valid

**Message:** `The master key in <source> is not valid. It must be the base64 form of exactly 32 bytes (for example the output of 'openssl rand -base64 32').`

The source is `Vellum__MasterKey`, `Vellum__PreviousMasterKey`, the file that a `…File` variable names, or the generated key file.

**Cause:** The value is not base64, or it does not decode to exactly 32 bytes. A frequent cause is a value that was cut, or a hex string.

**Fix:**

1. If this instance has stored credentials, find the correct key. Do not make a new key: a new key cannot read the stored rows.
2. For a new instance, make a key with `openssl rand -base64 32`.
3. Set the value with no quotation marks inside it.
4. Start again.

## A key and a key file are both set

**Message:** `Vellum__MasterKey and Vellum__MasterKeyFile are both set. Set one of them.`

The same message exists for `Vellum__PreviousMasterKey` and `Vellum__PreviousMasterKeyFile`.

**Fix:**

1. Remove one of the two variables.
2. Start again.

## A key file cannot be read

**Message:** `Vellum__MasterKeyFile names a file that cannot be read (<error>). Mount the file and make it readable by the app user.`

**Cause:** The file does not exist in the container, or the app user has no read permission.

**Fix:**

1. Check that the file is mounted at the path that the variable names.
2. Give the app user read permission on the file.
3. Start again.

## The hosted profile has no master key

**Message:** `The hosted profile needs a master key. Set Vellum__MasterKey to the base64 form of 32 random bytes (for example the output of 'openssl rand -base64 32'), or set Vellum__MasterKeyFile to a file that holds that value.`

**Cause:** The hosted profile never creates a key.

**Fix:**

1. Set `Vellum__MasterKey` or `Vellum__MasterKeyFile`.
2. Start again.

## The database holds rows under a key that is not loaded

**Message:** `The database holds rows that are encrypted under a master key that is not loaded: <keys>. The loaded keys are: <ids>. Vellum does not start, because it cannot read these rows.`

The message gives the ID of each missing key and the number of rows that use it.

**Cause:** The master key in the environment is not the key that encrypted the stored credentials or the session keys. This occurs after a restore to a machine with another key, or after a key change without the previous key.

**Fix:**

1. Start Vellum with the missing key as `Vellum__MasterKey`.
2. If you are changing the key, keep the new key as `Vellum__MasterKey` and set the missing key as `Vellum__PreviousMasterKey`. See [Rotate the master key](/how-to/rotate-the-master-key/).
3. If the key is lost, see [Lost master key](/runbooks/lost-master-key/). The command `dotnet Vellum.dll keys forget --yes` deletes the rows that no loaded key can read.

## No key file, and the database holds data

**Message:** `The master key file '<path>' does not exist, and the database holds rows that are encrypted under another master key: <keys>. Vellum does not create a new key, because a new key cannot read these rows.`

**Cause:** The local profile has no key in the environment and no `master.key` in the data directory, and the database is not empty. This occurs when the data volume is new and the database volume is old.

**Fix:**

1. Restore `master.key` from a backup to the data directory. As an alternative, set `Vellum__MasterKey` to the key that was used.
2. Start again.
3. If the key is lost, see [Lost master key](/runbooks/lost-master-key/).

## Other users can read the key file

**Message:** `The master key file '<path>' can be read or changed by other users of this machine. Run 'chmod 600 <path>' and start again. If another user can have read the key, rotate the master key.`

**Cause:** The group or other users have a permission on `master.key`.

**Fix:**

1. Run `chmod 600` on the file.
2. Start again.
3. If another user can have read the key, rotate it: [Rotate the master key](/how-to/rotate-the-master-key/).

## The key file is empty

**Message:** `The master key file '<path>' is empty. If this instance has no stored secrets yet, delete the file and start again. Otherwise restore the file from a backup.`

**Fix:**

1. If the instance has no stored secrets, delete the file.
2. If it has stored secrets, restore the file from a backup.
3. Start again.

## The data directory cannot be used

**Message:** `The master key file '<path>' cannot be read or created (<error>). Make Vellum__DataDirectory a directory that the app user can write, or set Vellum__MasterKey.`

A second message is for the setup code: `The setup code file '<path>' cannot be read or created (<error>). Make Vellum__DataDirectory a directory that the app user can write, or set Vellum__SetupToken.`

**Cause:** The app user cannot write the directory that `Vellum__DataDirectory` names. The default is `./data`.

**Fix:**

1. Mount a volume at the data directory.
2. Give the app user write permission on it.
3. Start again.

## The setup code file is empty or too short

**Message:** `The setup code file '<path>' is empty or too short. Delete the file and start again.`

**Fix:**

1. Delete the file `setup-code` in the data directory.
2. Start again. Vellum creates a new setup code.

## The secure cookie value is not valid

**Message:** `Vellum__SecureCookies is not valid. Set it to 'true' or 'false'.`

**Fix:**

1. Set the value to `true` or `false`, or remove it.
2. Start again.

## The hosted profile refuses plain cookies

**Message:** `The hosted profile sends the session cookie over HTTPS only. Remove Vellum__SecureCookies=false and serve the app over HTTPS.`

**Fix:**

1. Remove `Vellum__SecureCookies`.
2. Put a reverse proxy with HTTPS in front of Vellum.
3. Start again.

## The hosted profile refuses the allowed hosts

**Message:** `The hosted profile does not accept every host name. Set AllowedHosts to the public host names of this instance, separated by ';'. Do not use '*', a name that contains '*', '0.0.0.0', '::', '[::]' or an empty entry.`

**Cause:** `AllowedHosts` is not set, or one entry of the list accepts each host name. The position of the entry does not matter. A `;` at the end makes an empty entry.

**Fix:**

1. Set `AllowedHosts` to the public host names, for example `vellum.example.com;vellum.internal.example.com`.
2. Start again.

## The setup token is too short

**Message:** `Vellum__SetupToken is too short. Set it to at least 16 random characters. In the local profile you can also remove it; Vellum then generates a setup code.`

**Fix:**

1. Set a token of 16 characters or more. In the local profile you can remove the token.
2. Start again.

## The hosted profile has no user and no setup token

**Message:** `The hosted profile has no user yet and no setup token. Set Vellum__SetupToken to at least 16 random characters and give it to the person who runs first-time setup.`

**Fix:**

1. Set `Vellum__SetupToken`.
2. Start again.
3. Give the token to the person who does the setup.

## The trusted proxies value is not valid

**Message:** `Vellum__TrustedProxies is not valid. Set it to the addresses or CIDR ranges of your reverse proxies, separated by ';', for example '10.0.0.5;172.20.0.0/16'. A range that holds every address is not accepted.`

**Cause:** One entry of `Vellum__TrustedProxies` is no IP address and no CIDR range. Or it is a range with the prefix length 0, such as `0.0.0.0/0`. A host name is not accepted.

**Fix:**

1. Set each entry to the address that Vellum sees for a reverse proxy, or to the narrowest range that holds it.
2. Separate the entries with `;`.
3. With no reverse proxy, remove the variable.
4. Start again.

With `docker-compose.hosted.example.yml` you set `VELLUM_TRUSTED_PROXIES`. See [Run in hosted mode](/how-to/run-in-hosted-mode/#configure-the-reverse-proxy).

## The telemetry endpoint is not valid

**Message:** `OTEL_EXPORTER_OTLP_ENDPOINT is not valid. Set it to the http or https address of an OTLP collector, for example http://otel-collector:4317, or remove it to turn telemetry off.`

**Cause:** The value is not an absolute URL with the scheme `http` or `https`. A frequent cause is a value with no scheme, such as `otel-collector:4317`.

**Fix:**

1. Set the value to the address of the collector with its scheme, or remove the variable.
2. Start again.

## The database cannot be reached

**Message, local profile with no key file:** `Vellum has no master key file yet and cannot reach the database to check that no row is encrypted under another key (<error>). Start the database, or check ConnectionStrings__DefaultConnection, and start again.`

**Message, other cases:** a line that starts with `Vellum stopped on an error.` and gives the error of the database driver.

**Cause:** Postgres is not running, the host name or the password in `ConnectionStrings__DefaultConnection` is wrong, or Vellum started before the database was ready.

**Fix:**

1. Check the database: `docker compose ps`. The Compose file starts Vellum only after the database is healthy.
2. Check the host, the database name, the user and the password in `ConnectionStrings__DefaultConnection`.
3. Start again.

## DBX001

**Message:** an error with the code `DBX001` and the text `The Deedbox schema is missing or older than this build`.

**Cause:** The event store checks its tables when Vellum starts. The tables are missing or older than this version. Vellum applies its migrations on each start, so this error occurs only when `Database__MigrateOnStartup` is `false` and no other step applied the migrations.

**Fix:**

1. Remove `Database__MigrateOnStartup=false`, or apply the migrations with the step that your deployment uses.
2. Start again.

More than one instance can start at the same time with migrations on. Each instance waits for a database lock before it applies them.

## The app starts, but each request gets 400

**Symptom:** The log shows no refusal. The browser gets `400` for each page.

**Cause:** The host name of the request is not in `AllowedHosts`. The web server refuses the request before Vellum reads it. The answer does not come from Vellum.

**Fix:**

1. Add the host name that the browser uses to `AllowedHosts`.
2. If a reverse proxy changes the `Host` header, configure the proxy to pass the original host name.
3. Start again.

## A warning that is not a refusal

`The key file <path> is not used, because the environment gives the master key. Keep it only as a backup of an old key.`

Vellum starts. The environment key is in use and the `master.key` file is not read.

## See also

- [Bootstrap environment](/reference/bootstrap-environment/)
- [Diagnostics and logs](/runbooks/diagnostics-and-logs/)
- [Setup and sign-in problems](/troubleshooting/setup-and-sign-in/)
