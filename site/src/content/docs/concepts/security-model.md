---
title: Security model
description: How Vellum separates workspaces, authenticates people and agents, checks requests, protects credentials, limits outbound requests and treats untrusted input, and what it does not protect against.
---

This page explains the security design of Vellum: what each control does, why it is there, and where it stops.

## Tenant isolation

The workspace is the boundary of access. A project belongs to one workspace. A model, a scenario, a promise, a scan, a credential and a setting belong to one project or one workspace.

Access comes from a membership and from nothing else. A membership gives one user one role in one workspace: viewer, editor or owner. Each role includes the roles below it.

Each endpoint that names a workspace or a project checks the membership of the caller before it reads or writes. For a project, Vellum first finds the workspace of the project and then checks the role in that workspace. A user with no membership in the workspace gets `403`, with any ID in the request. The check is on the server, on each request. The web client hides controls for convenience only.

Three rules complete this.

- **The instance administrator role gives no access to a workspace.** An instance administrator manages the policy, the defaults, the keys and the diagnostics. To read a project, the administrator needs a membership like any other user.
- **An ID from another workspace is not used.** For example, a model import never reads or changes a schema of another project. When a document carries the ID of such a schema, the import gives the schema a new ID in its own project.
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

A sign-in with GitHub takes the email address from GitHub, and only the primary address that GitHub verified. Vellum does not take the public address of a profile, because the owner of the profile types that text. A sign-in with a provider never takes over an account that exists: Vellum refuses it with `409`. A provider that the operator did not configure answers `404`.

### Requests that change state

`SameSite=Strict` knows sites, not origins. A page on a sibling host name, for example `other.example.com` beside `vellum.example.com`, is the same site, so the browser sends the cookie with a request from that page. So Vellum also checks the origin of each request that can change state. That is each request with a method other than `GET`, `HEAD`, `OPTIONS` and `TRACE`.

- With a `Sec-Fetch-Site` header, the value must be `same-origin` or `none`. Each current browser sends this header, and a page cannot set it.
- With no `Sec-Fetch-Site` header and with an `Origin` header, the host and the port in `Origin` must be the host and the port of the request. Vellum does not compare the scheme, because behind a proxy that terminates TLS the app sees `http`.
- A request with neither header passes. It does not come from a page in a current browser. It comes from `curl`, a script or an agent, and such a caller carries no cookie of another person.
- A request that presents an agent token is not checked. The token authenticates it, never the cookie.

A request that fails the check gets `403` with the type `cross_site_request`. Nothing is changed.

### Limits on guessing

Each limit counts for one client address in one minute. Over the limit, the answer is `429` with the type `rate_limited` and a `Retry-After` header.

| Requests | Limit |
|---|---|
| `POST /api/auth/login`, `POST /api/setup`, and the start and the completion of a sign-in with a provider | 10 calls for each endpoint |
| `POST /api/auth/register` | 10 calls |
| Each request with no valid credential to `/api`, `/mcp`, `/llms.txt`, `/llms-full.txt` or `/skills` | 300 calls together |

The last limit also counts a request with a wrong bearer token. It does not count the health endpoints or the files of the web client. A request with a valid session or a valid agent token is not counted. The limit of an endpoint and the limit of 300 both apply.

- Five wrong passwords lock an account for five minutes.
- Sign-in gives the same `401` for a wrong password, an unknown account and a locked account. The answer does not tell an attacker which accounts exist.

### Behind a reverse proxy

Vellum reads `X-Forwarded-For` and `X-Forwarded-Proto` only from the senders that `Vellum__TrustedProxies` names. The value is a list of addresses or CIDR ranges, separated by `;`. From each other sender Vellum ignores the two headers. Vellum reads only the last hop, which is the value that the trusted proxy wrote.

The reason is that a client writes these headers as easily as a proxy does. A server that trusts them from every sender lets a client select the address that the rate limit counts, and lets a client claim HTTPS.

Without `Vellum__TrustedProxies`, Vellum ignores the headers. All clients of a proxy then share one limit. Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`: the web framework reads it and then trusts the headers from every sender. See [Run in hosted mode](/how-to/run-in-hosted-mode/#configure-the-reverse-proxy).

### The first account

The first account becomes the instance administrator. So setup needs proof that the caller operates the instance: a one-time setup code from the log, or the setup token that the operator set. A hosted instance with no user and no setup token does not start. See [Local and hosted profiles](/concepts/local-and-hosted-profiles/).

### Agent tokens

A coding agent authenticates with a bearer token that starts with `vlm_agt_`. Vellum shows the token one time, when an owner creates it, and stores only a digest of it.

A token is a weaker identity than the user who made it.

- It works in one workspace. An owner can restrict it to one project.
- It has scopes: `read`, `propose`, `export`, `scan-pull-request`, `push`.
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

A New Relic Source has no URL setting. Its key goes only to `https://api.newrelic.com` or `https://api.eu.newrelic.com`, chosen by the region of the Source. The key is stored envelope-encrypted and bound to that origin, like every other credential, so even a changed database row cannot send it elsewhere. Vellum sends NerdGraph queries only, never a mutation, so a read-only User API key is enough. A trace upload is read as a stream and never written to disk or to the database; only the derived services and edges are kept. See [Runtime sources](/reference/runtime-sources/).

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

### What `vellum scan` sends

`vellum scan` runs the extractors on the machine that holds the code. It sends these items and nothing else:

- the manifest: the path, the size and the git blob SHA of each file that the extractors read whole;
- the path and a reason code of each file that the scope would read and that was not read, such as `secret-file` or `limit:file-bytes`;
- the facts, with their citations as path and lines;
- the extraction rules and their versions, the scope that selected the files, and the commit that `.git/HEAD` names.

It does not send the content of a file, and it does not send the path of a file that the path filter refuses by its name, such as `.env`. A fact can hold a value that the source states, such as a route, an image name or a schema. The credential guard runs on the machine before a value enters a fact. Run `vellum scan --dry-run --payload push.json` to read the exact body before you push.

The server takes a push as untrusted input. It checks the token, the scope `push` and the editor role of its user. It refuses rules that are not its own, a file that the scope rules do not read, a citation of a file that is not in the manifest, and a push over the limits of the workspace. A push makes proposals. It cannot accept one, and it cannot change an accepted fact. Vellum records the token, the reported commit and "content not held" with the Snapshot, and does not verify the reported commit.

A project document is untrusted text too. Vellum parses it to a tree and builds the page from the tree. It generates no code from a document and evaluates none. An expression, an `import`, an event handler and a `script` tag in a document have no effect. See [Write project documents](/how-to/write-project-docs/#know-what-the-text-can-contain).

## The browser

Vellum sends these headers with each response.

| Header | Value | Effect |
|---|---|---|
| `Content-Security-Policy` | See below. | The page loads code and data from its own origin only. |
| `X-Content-Type-Options` | `nosniff` | The browser does not guess the type of a file. |
| `X-Frame-Options` | `DENY` | No page can show Vellum in a frame. The one exception is the embed page of a view link, below. |
| `Referrer-Policy` | `no-referrer` | A link from Vellum does not tell the other site the address of the page. |
| `Cross-Origin-Opener-Policy` | `same-origin` | A window of another origin gets no reference to the Vellum window. |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=(), payment=()` | The page cannot use these functions of the browser. |
| `Cache-Control` | `no-store`, on `/api` and `/mcp` only | No shared cache and no browser history keeps an answer of the API. |
| `Strict-Transport-Security` | `max-age=31536000`, in the hosted profile only, and only on a request that came over HTTPS | The browser uses HTTPS for the host name for one year. |

The content security policy is:

```text
default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

- Scripts come from the origin of the app only. The policy has no `'unsafe-inline'` and no `'unsafe-eval'` for scripts. So text that reaches the page cannot run as a script.
- `'wasm-unsafe-eval'` lets the code highlighter compile its grammar engine. It allows no script text.
- Styles need `'unsafe-inline'`, because the canvas and the editor position their parts with style attributes.
- The page connects to its own origin only. A script in the page cannot send data to another host.
- The page `/embed/{token}` of a view link that answers now sends `frame-ancestors` with the origins of the workspace setting `sharing.embedHosts`, and no `X-Frame-Options`. With an empty list it sends `'none'` like every other page. See [Share and embed a read-only page](/how-to/share-and-embed/).

The local profile sends no `Strict-Transport-Security` header. A local instance on plain HTTP must not make a browser use HTTPS for its host name. In the hosted profile, the scheme of a request comes from a trusted proxy: see [Behind a reverse proxy](#behind-a-reverse-proxy).

In the `Development` environment, the pages of the API reference under `/scalar` and `/openapi` have no content security policy. The container image does not serve them.

## The container

The image and the two Compose files limit what the app process can do.

- **Not root.** The process runs as the app user of the image, not as root.
- **Read-only root file system.** The process cannot change the program or write a file beside it. It writes to the data volume on `/data` and to a temporary directory in memory on `/tmp`, and to nothing else.
- **No Linux capability.** The Compose files drop all capabilities.
- **No new privileges.** A program that the process starts cannot get more privileges than the process has.

`docker-compose.yml` and `docker-compose.hosted.example.yml` set the last three. A container that you start with `docker run` has them only when you give the options: see [Run in hosted mode](/how-to/run-in-hosted-mode/#start-one-instance-with-docker-run).

Both Compose files publish the app port on `127.0.0.1` only, unless you set `VELLUM_BIND`.

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
