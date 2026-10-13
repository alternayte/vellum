---
title: Setup and sign-in problems
description: Symptoms, causes and fixes for the first-run setup, the setup code, sign-in, registration and session cookies.
---

Each entry gives the symptom, the cause and the fix. An error answer of the API has the fields `type`, `title`, `detail` and `errors`.

## I cannot find the setup code

**Symptom:** The setup screen asks for a **Setup code**.

**Cause:** Setup needs proof that you operate the instance. In the local profile with no `Vellum__SetupToken`, Vellum creates a code when it starts with no user.

**Fix:**

1. Read the log and find the line that contains `VELLUM SETUP CODE:`.

   ```sh
   docker compose logs vellum | grep "VELLUM SETUP CODE:"
   ```

2. As an alternative, read the file `setup-code` in the data directory. With the Compose file the data directory is `/data`.

   ```sh
   docker compose exec vellum cat /data/setup-code
   ```

3. Enter the code in **Setup code**.

If the operator set `Vellum__SetupToken`, the log has no code. The screen then says **The value of Vellum__SetupToken that the operator set for this deployment.** Enter that value. The hosted profile always uses `Vellum__SetupToken`.

`GET /api/setup/status` tells you which one applies: `setupTokenHint` is `log` or `operator`.

## The setup code is refused

**Symptom:** `POST /api/setup` answers `403` with the type `invalid_setup_token` and the title `The setup token is missing or wrong`.

**Cause:** The code is not the code of this instance. A frequent cause is a code from an older log of another container.

**Fix:**

1. Read `detail` in the answer. It says where the correct value is: the log and the `setup-code` file, or `Vellum__SetupToken`.
2. Copy the code again from the newest start in the log. Copy no spaces.
3. Try again.

## Setup says the instance is already set up

**Symptom:** `POST /api/setup` answers `409` with the type `already_set_up` and the title `This instance is already set up`.

**Cause:** A user exists. The setup code works one time. Vellum deletes the `setup-code` file when setup is complete.

**Fix:**

1. Sign in with the account that was created in setup.
2. If nobody knows that account, restore a backup or start with an empty database. Vellum has no command that resets a password.

## Setup or sign-in answers 429

**Symptom:** `POST /api/setup` or `POST /api/auth/login` answers `429`. The answer has a `Retry-After` header. The sign-in screen shows **Invalid email or password**, because it shows this text for each failed sign-in.

**Cause:** These two endpoints accept 10 calls in one minute from one client address.

**Fix:**

1. Wait for the number of seconds in `Retry-After`.
2. Try again.
3. If all users get `429` behind a reverse proxy, Vellum sees the address of the proxy as the client. Set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` and start again.

## The password is refused

**Symptom:** Setup shows a message about the password. Registration shows **Registration failed. Please try again.** The API answers `400` with the type `validation_error` and one entry in `errors` for each rule that failed.

**Cause:** A password has at least 10 characters, with an upper-case letter, a lower-case letter and a digit.

**Fix:**

1. Choose a password that obeys the rule.
2. Try again.

## Sign-in shows Invalid email or password

**Symptom:** The sign-in screen shows **Invalid email or password**. `POST /api/auth/login` answers `401`.

**Cause:** One of these. Vellum gives the same answer for each, so an attacker cannot tell which accounts exist.

- The password is wrong.
- No account has this email address.
- The account is locked. Five wrong passwords lock an account for five minutes.
- The answer was `429`. The screen shows the same text. See the entry above.

**Fix:**

1. Check the email address.
2. If you tried five times or more, wait five minutes. Each sign-in attempt during the lock fails, also with the correct password.
3. Try again with the correct password.

Vellum has no password reset by email.

## Each request answers 401 after sign-in

**Symptom:** Sign-in succeeds. The next page sends you back to sign-in, or the API answers `401`.

**Cause:** The browser does not send the session cookie. The cookie is `HttpOnly` and `SameSite=Strict`.

- With `Vellum__SecureCookies=true`, or in the hosted profile, the cookie is always Secure. A browser does not send a Secure cookie over plain `http`, except to `localhost`.
- Behind a reverse proxy without `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, Vellum sees each request as plain `http`.
- A page on another site that calls the API does not get the cookie, because of `SameSite=Strict`.

**Fix:**

1. Open the app over `https`.
2. Behind a reverse proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. Make the proxy send the `X-Forwarded-Proto` and `X-Forwarded-For` headers.
3. In the local profile on a trusted network with plain `http`, remove `Vellum__SecureCookies` or set it to `false`.
4. For `curl`, keep the cookie in a file and send it with each request: [Use the HTTP API](/how-to/use-the-http-api/). An agent uses an agent token and not the cookie: [Connect a coding agent](/how-to/connect-a-coding-agent/).

A session ends 7 days after the last request.

## Registration fails

**Symptom:** The registration screen shows **Registration failed. Please try again.** It shows this text for each cause.

**Cause:** Read the answer of `POST /api/auth/register` in the network panel of the browser.

| Status | Type | Cause |
|---|---|---|
| `403` | `registration_closed` | The instance administrator has not opened self-registration. In the local profile, registration is off after setup. |
| `403` | `setup_required` | The instance has no user. Complete first-run setup first. |
| `400` | `validation_error` | The password does not obey the rule, or an account with this email address exists. `errors` has the reason. |

**Fix:**

1. For `registration_closed`, ask the instance administrator to switch on **Let people register themselves** in the instance settings: [Configure settings](/how-to/configure-settings/).
2. For `setup_required`, complete the setup.
3. For `validation_error`, read `errors` and correct the value.

Limit: when registration is closed, nobody can add an account. Vellum has no endpoint that lets an administrator create an account for another person. The hint of the setting says **With this off, only an administrator adds accounts.** That is not possible at this time. Switch the setting on for the time that the person needs to register, then switch it off.

## Sign-in with GitHub or Google answers 409

**Symptom:** After the provider page, `GET /api/auth/external/callback` answers `409` with the type `account_exists` and the title `An account with this email address already exists`.

**Cause:** An account with a password has the same email address. Vellum does not link a provider to an existing account.

**Fix:**

1. Sign in with the email address and the password of that account.

## Sign-in with GitHub or Google fails in another way

| Symptom | Cause | Fix |
|---|---|---|
| `400` with `Email not provided by external provider` | The provider gave no email address. | Sign in with a password. |
| `403` with the type `registration_closed` | A first sign-in with a provider creates an account, and registration is closed. | Ask the instance administrator to open registration. |
| `403` with the type `setup_required` | A provider sign-in never creates the first account. | Complete first-run setup. |
| The button does nothing useful | The provider is not configured. | Set `Authentication__GitHub__ClientId` and `Authentication__GitHub__ClientSecret`, or the two `Authentication__Google__…` values. |

Limit: sign-in with GitHub is not tested and is expected to fail with `Email not provided by external provider`. The GitHub registration uses the path of the callback route of the app as its own callback path, and it maps no email address.

## See also

- [Vellum does not start](/troubleshooting/start-up-refusals/)
- [Bootstrap environment](/reference/bootstrap-environment/)
- [Self-host quickstart](/tutorials/self-host-quickstart/)
