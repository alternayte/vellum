---
title: Local and hosted profiles
description: Why Vellum is one program with two operating profiles, what the profiles change, and the status of the hosted profile.
---

This page explains the two operating profiles of Vellum and the reason for each difference between them.

## One program

Vellum is one program and one container image. The value `Vellum__Profile` selects the profile at start-up: `local` or `hosted`. The default is `local`.

The two profiles have the same features, the same data model and the same database schema. No feature exists in one profile only. A workspace, a project and a model are the same thing in both.

A profile changes two things only:

- What Vellum requires from the environment before it starts.
- The built-in defaults of the instance policy.

Vellum reads the profile from the environment and nowhere else. No screen and no API call changes it.

## The assumption behind each profile

The local profile assumes one operator on a machine or a network that the operator controls. The operator is the first user, and the operator trusts the people who can reach the instance.

The hosted profile assumes that the instance is on the public internet and that people whom the operator does not know can create accounts and workspaces. A user of one workspace must be treated as a possible attacker of another workspace and of the network behind the instance.

Each difference below follows from one of these two assumptions.

## What differs

| | `local` | `hosted` | Reason |
|---|---|---|---|
| Master key | Vellum creates `master.key` in the data directory when the environment gives no key. | Required from the environment. Vellum does not start with no key. | A local start needs no configuration. A hosted instance must keep its key outside the data volume, under the control of the operator. |
| First account | Vellum writes a one-time setup code to the log and to the file `setup-code`. `Vellum__SetupToken` is optional. | `Vellum__SetupToken` is required while no user exists. | The first account is the instance administrator. On a public address the first caller must not get it. On a local machine the person who reads the log is the operator. |
| Session cookie | Secure when the request came over HTTPS. | Always Secure. `Vellum__SecureCookies=false` is refused. | A local instance runs on `http://localhost`. A hosted instance must not send a session over plain HTTP. |
| `AllowedHosts` | Any value, `*` by default. | The public host names. `*` and the addresses that mean every host are refused. | A public instance must not answer for a host name that an attacker selects. |
| Self-registration | Off by default. | On by default. | A local instance has the people that its operator adds. A hosted instance is open for sign-up. |
| URLs on private networks | Allowed by default. | Refused by default. | A local model server such as Ollama is on a private address. On a hosted instance, a settings URL that points at a private address is an attack on the network of the operator. |
| Plain `http` in a settings URL | Allowed. | Refused. It is accepted only for a private host, and only when the policy allows private network URLs. | A credential goes to each settings URL. On a public network it must not go in clear text. |
| Sending private code to AI | A workspace can turn it on. | Refused until the instance administrator allows it. | On a hosted instance the operator decides first if code of customers can leave the instance. |

The three policy rows are defaults. The instance administrator changes them under **Settings** → **Instance**, in both profiles. The other rows are start-up rules and cannot be changed in the app.

The exact values are in [Bootstrap environment](/reference/bootstrap-environment/) and [Settings](/reference/settings/#the-instance-policy). The start-up refusals and their fixes are in [Start-up refusals](/troubleshooting/start-up-refusals/).

## What does not differ

- **Tenant isolation.** A workspace is the boundary of access in both profiles. A local instance with one workspace runs the same checks as a hosted instance with many. See [Security model](/concepts/security-model/).
- **Encryption of credentials.** Both profiles encrypt each stored credential under the master key.
- **Setup needs proof.** Both profiles require a setup code or a setup token for the first account.
- **Refusal to start with the wrong key.** Both profiles stop when the database holds rows under a master key that is not loaded.
- **What leaves the instance.** Both profiles send nothing to a third party by default. See [What leaves your instance](/concepts/what-leaves-your-instance/).
- **The line between an agent and a person.** See [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

The reason is that a second code path for security is a second place for a fault. The hosted profile adds requirements. It does not add a different mechanism.

## Why the local profile is not "the unsafe one"

The local profile makes a first start possible with no configuration: `docker compose up`, read the setup code, create the administrator. It does this with defaults that fit a private machine.

It does not turn checks off. It still refuses a master key file that other users of the machine can read. It still requires the setup code. The Compose file publishes the app and the database on `127.0.0.1` only.

If you publish a local instance on a network that you do not control, the local defaults no longer fit. In particular, turn **Allow URLs on private networks** off when people whom you do not know can change settings.

## Status of the hosted profile

The hosted, multi-tenant profile exists in the code. It is not ready for use.

The start-up rules and the policy defaults on this page are in the code. They are not a statement that the profile is safe to operate for other people.

Use the local profile to self-host. [Run in hosted mode](/how-to/run-in-hosted-mode/) describes how to start the hosted profile and what it requires.
