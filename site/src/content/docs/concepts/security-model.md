---
title: Security model
description: How Vellum separates workspaces, authenticates people and agents, protects credentials, limits outbound requests and treats untrusted input, and what it does not protect against.
---

This page explains the security design of Vellum: what each control does, why it is there, and where it stops.

## Tenant isolation

The workspace is the boundary of access. A project belongs to one workspace. A model, a scenario, a promise, a scan, a credential and a setting belong to one project or one workspace.

Access comes from a membership and from nothing else. A membership gives one user one role in one workspace: viewer, editor or owner. Each role includes the roles below it.

Each endpoint that names a workspace or a project checks the membership of the caller before it reads or writes. For a project, Vellum first finds the workspace of the project and then checks the role in that workspace. A user with no membership in the workspace gets `403`, with any ID in the request. The check is on the server, on each request. The web client hides controls for convenience only.

Three rules complete this.

- **The instance administrator role gives no access to a workspace.** An instance administrator manages the policy, the defaults, the keys and the diagnostics. To read a project, the administrator needs a membership like any other user.
- **An ID from another workspace is refused, not used.** For example, a model import refuses a schema ID that belongs to another project.
- **A credential is bound to its workspace.** The encrypted row of a credential does not decrypt under another workspace ID. See [Secrets](#secrets).

Tests in the repository sign in as a user of a second workspace and call the endpoints of the first with its real IDs. Each call must be refused.

## Authentication

### People

A person signs in with an email address and a password, or with GitHub or Google when the operator configured them. A session is a cookie.

- The cookie is `HttpOnly` and `SameSite=Strict`. A script in the page cannot read it, and a browser does not send it with a request that comes from another site.
- In the hosted profile the cookie is always `Secure`. In the local profile it is `Secure` when the request came over HTTPS.
- A session ends after 7 days with no use.
- The keys that sign the cookie are in the database, each encrypted under the master key.

A new password has 10 characters or more, with a digit and an upper-case letter.

### Limits on guessing

- `POST /api/auth/login` and `POST /api/setup` accept 10 calls in one minute from one client address. Then they answer `429` with a `Retry-After` header.
- Five wrong passwords lock an account for five minutes.
- Sign-in gives the same `401` for a wrong password, an unknown account and a locked account. The answer does not tell an attacker which accounts exist.

Behind a reverse proxy, the rate limit counts by the client address only when the proxy passes it on and `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is `true`. If not, all clients of the proxy share one limit.

### The first account

The first account becomes the instance administrator. So setup needs proof that the caller operates the instance: a one-time setup code from the log, or the setup token that the operator set. A hosted instance with no user and no setup token does not start. See [Local and hosted profiles](/concepts/local-and-hosted-profiles/).

### Agent tokens

A coding agent authenticates with a bearer token that starts with `vlm_agt_`. Vellum shows the token one time, when an owner creates it, and stores only a digest of it.

A token is a weaker identity than the user who made it.

- It works in one workspace. An owner can restrict it to one project.
- It has scopes: `read`, `propose`, `export`.
- It acts for the user who created it and has no more access than that user has today. When that user leaves the workspace, the token stops.
- It is refused on each endpoint that does not explicitly accept tokens. The default for a new endpoint is refusal.
- It cannot call an endpoint that is for a person only, with any scope. The answer is `403` with the type `human_only`.

Vellum decides if a request is from a person or from an agent by the way the request authenticated. A request cannot state that it is a person. [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/) explains the reason for the line.

## Secrets

Vellum stores three kinds of credential for a workspace: the GitHub token, the API key of each AI provider and the webhook signing secret. [Settings](/reference/settings/#secret-names) lists the names.

### Envelope encryption

Each credential is encrypted with AES-256-GCM under a random data key of its own. The data key is encrypted under the master key. The master key comes from the environment or from a key file. It is never in the database.

This has two effects. A copy of the database alone gives no credential. And a rotation of the master key wraps each data key again and does not need the old values in clear text. During a rotation the previous master key only decrypts.

The encrypted row is bound to its workspace, its name, the origin that it was saved for and its fingerprint. A person who can write to the database, and who copies a row to another workspace or changes its origin, gets a row that does not decrypt.

No endpoint returns a credential. The app reads a credential in one place, to put it in the request that it is for. It does not go into a log, a response, an error or a prompt.

Vellum does not start when the database holds rows under a master key that is not loaded. It does not create a new key in that case, because a new key can read none of them. See [Rotate the master key](/how-to/rotate-the-master-key/) and the runbook [Lost master key](/runbooks/lost-master-key/).

### The fingerprint

An owner must be able to see which value is stored, and Vellum never shows the value. So it shows a fingerprint: the first 8 hexadecimal digits of an HMAC-SHA-256 of the value, under a key that is derived from the master key for the workspace.

A plain hash would let a reader of the database test guesses, and would show that two workspaces use the same key. The keyed digest does neither. The same value in two workspaces has two fingerprints. The fingerprint changes when the master key changes.

### Origin binding

A credential is saved for one origin: the scheme, the host and the port of the URL that it is for. Vellum gives the credential to a request only when the request goes to that origin.

Without this, a person who can change settings could change the GitHub base URL to a server of their own and receive the stored token on the next import. With it, a changed URL gets no credential. The owner must enter the value again to bind it to the new origin, and that needs the value.

A GitHub token and an AI API key go in an HTTP header. Vellum accepts only printable ASCII for them. A line break in such a value would add a header to the request.

## Outbound requests

Vellum makes requests to URLs that users supply: the GitHub API, AI providers and the price API. A server that fetches a URL for a user can be made to fetch an address inside the network of its operator. This is server-side request forgery (SSRF). The cloud metadata address is the usual target.

One component decides if the server can call a URL. It applies these rules.

- The URL is absolute, with the scheme `http` or `https`, and has no user name or password.
- When the instance policy refuses private network URLs, each address of the host must be an address that the public internet routes. Loopback, private, link-local, shared, reserved and multicast addresses are refused, in IPv4 and in IPv6, and also an IPv6 address that carries one of those IPv4 addresses.
- In the hosted profile the scheme must be `https`. Plain `http` passes only for a host whose addresses are all private, and only when the policy allows private networks.

The check runs three times, because each time closes a different gap.

1. **When the URL is saved.** A bad URL is refused with a reason.
2. **Each time the settings are read.** A stricter instance policy then applies at once to URLs that were saved before. A blocked URL is not used, and no credential goes to it.
3. **When the request is made.** Vellum resolves the host name again, checks the addresses, and connects to exactly the address that it checked. A name that changes its answer after the check (DNS rebinding) cannot move the connection.

The transport does not follow redirects, keeps no cookies and does not use the proxy settings of the machine. A redirect could send a request, with its credential, to an address that was never checked.

A local model server is on a private address. So the local profile allows private network URLs by default, and the hosted profile refuses them. See [What leaves your instance](/concepts/what-leaves-your-instance/).

## Untrusted input

Text from a repository, text from an AI provider and text that an agent writes are data. They are never instructions, and they do not become facts without a person. See [The model is the source of truth](/concepts/model-is-the-source-of-truth/#proposals-never-write-silently).

Three controls stop a secret that is in a repository from moving further.

- **Content clearance.** A scan checks the bytes of each file when they arrive from GitHub, before it stores them. Vellum does not store binary content, a manifest that declares a Secret, text that holds a private key, or text in which many lines look like credentials. It keeps only the reason. The check depends on the content alone, not on the path.
- **The credential guard.** The extractors do not read a value in the places where a secret can be: command arguments, environment values, schema defaults and examples, and the user part of a URL. As a second line, each key, name and attribute that an extractor reads passes a check for the shape of a credential before it enters a fact. See [Extraction rules](/reference/extraction-rules/).
- **The secret redactor.** Before a prompt goes to an AI provider, Vellum replaces text that looks like a credential. This is always on.

The redactor works on the shape of a value and on the name beside it. It does not find a short password in prose or a secret under a name that says nothing. It is a second line against a credential that somebody committed by mistake. It is not the control that protects private code. That control is the setting `privacy.sendPrivateCodeToAi`: with it off, Vellum refuses a request to an AI provider that holds text from a private repository.

Generated documents apply the same idea in the other direction. The SDD and the brief mark each text from the model as data and escape it, so that a name in a model cannot form an instruction or a heading for the agent that reads the document.

Each request body has a size limit, and a body over the limit gets `413` before the endpoint parses it.

## The audit log

Vellum records who changed a sensitive thing, for example settings, credentials, keys, members, agent tokens and the price book. An entry has the time, the action, the target and the account. Entries cannot be changed or deleted through the app.

An entry holds no secret value. A settings entry names the fields that changed. For a URL field it also holds the new origin, because an auditor must see where a credential can now go.

The entry is written in the same database transaction as the change. A change is not saved without its entry, and an entry is not saved without its change.

The workspace log is for the owners of the workspace. The instance log, for example for a master key rotation, is for the instance administrator. See [Configure a workspace](/how-to/configure-settings/#read-the-audit-log).

## What Vellum does not protect against

- **An operator of the instance.** A person with the master key and the database reads every credential. A person with the database alone reads every model: only credentials and session keys are encrypted by Vellum. Encrypt the disk and limit access to the host.
- **A lost master key.** Vellum cannot recover the credentials. Back up the key apart from the database.
- **A compromised third party.** A token that you store works with the permissions that you gave it at GitHub. Use a fine-grained, read-only token. Text that you send to an AI provider is under the terms of that provider.
- **A secret with no shape.** The redactor and the clearance find credentials by pattern. They do not find all of them. Do not turn on **Send private repository code to AI providers** for a repository that holds secrets.
- **Plain HTTP in the local profile.** The local profile serves `http://localhost`. Put the app behind HTTPS before you publish it on a network.
- **A stolen agent token.** A token works until an owner revokes it or it expires. It cannot approve, accept or change settings, but it can read what its scopes allow.
- **A member who acts within a role.** An editor can change the model. The history shows who did what. Vellum does not ask for a second approval of a model change.
- **A wrong design.** A check that passed, a finding and a truth label state what Vellum looked at. None of them proves that a system is secure. See [Findings are not proof](/concepts/findings-are-not-proof/).

The hosted, multi-tenant profile exists in the code. It is not ready for use.
