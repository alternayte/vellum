---
title: Use the HTTP API
description: Sign in from a script, send a write, page through a list, read an error and send a settings version.
---

This guide shows you how to call the Vellum HTTP API from a terminal or a script. The web client uses the same API, so the API can do everything that the app does.

The examples need `curl`, `jq` and `uuidgen`. [HTTP API](/reference/http-api/) lists every endpoint.

## Select how to authenticate

| Caller | Credential | What it can call |
|---|---|---|
| A person, or a script that acts for a person | The session cookie from `POST /api/auth/login` | Every endpoint that the role of the account allows. |
| A coding agent | An agent token in the header `Authorization: Bearer <token>` | Only the endpoints that are open to agent tokens: reads, exports and draft proposals. Every other endpoint answers `403` with the type `human_only`. |

An agent token cannot add an element, accept a proposal, approve a promise or change a setting. For those calls, use the session cookie. [Connect a coding agent](/how-to/connect-a-coding-agent/) shows how to create an agent token and how to read what it can call.

## Sign in

1. Set the address of your instance and your account.

   ```sh
   export VELLUM_URL=http://localhost:8080
   export VELLUM_EMAIL=<email>
   export VELLUM_PASSWORD=<password>
   ```

2. Sign in. `-c` writes the session cookie to a file. `-b` sends it with each later call.

   <!-- snippet: live -->
   ```sh
   jq -n --arg email "$VELLUM_EMAIL" --arg password "$VELLUM_PASSWORD" '{email: $email, password: $password}' |
     curl -sS --fail-with-body -c vellum.cookies -H 'content-type: application/json' -d @- "$VELLUM_URL/api/auth/login" -o /dev/null
   curl -sS --fail-with-body -b vellum.cookies "$VELLUM_URL/api/auth/me" | jq -c '{email, displayName}'
   ```

Sign-in accepts 10 calls in one minute from one address. After that it answers `429` with a `Retry-After` header. Keep the cookie file and use it again. Do not sign in before each call.

The cookie file gives access to your account. Protect it like a password. `POST /api/auth/logout` ends the session.

## Create something

The caller selects the ID of each thing that it creates. An ID is a UUID. With an ID of your own, a script can send the same call again after a network error and not create a second copy.

<!-- snippet: live -->
```sh
WORKSPACE=$(curl -sS --fail-with-body -b vellum.cookies "$VELLUM_URL/api/workspaces" | jq -r '.[0].id')
PROJECT=$(uuidgen | tr '[:upper:]' '[:lower:]')
jq -n --arg id "$PROJECT" '{id: $id, name: "API example"}' |
  curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @- "$VELLUM_URL/api/workspaces/$WORKSPACE/projects" | jq -c '{name}'

ELEMENT=$(uuidgen | tr '[:upper:]' '[:lower:]')
jq -n --arg id "$ELEMENT" '{id: $id, kind: "system", name: "Shop", tags: []}' > element.json
curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @element.json "$VELLUM_URL/api/projects/$PROJECT/elements" |
  jq -c '{name, kind, factStatus}'
```

The last command prints:

```json
{"name":"Shop","kind":"system","factStatus":"declared"}
```

A create answers `201` with the new thing. A change is a `PATCH` or a `PUT` on the path of the thing, and a removal is a `DELETE`.

## Know the rule for writes with the cookie

Vellum checks where each request that can change state comes from. That is each request with a method other than `GET`, `HEAD`, `OPTIONS` and `TRACE`. The check stops a page of another origin that uses the session of a person in a browser.

| The request has | Vellum |
|---|---|
| No `Sec-Fetch-Site` header and no `Origin` header | Accepts it. |
| `Sec-Fetch-Site: same-origin` or `Sec-Fetch-Site: none` | Accepts it. |
| `Sec-Fetch-Site` with another value, for example `cross-site` or `same-site` | Refuses it with `403`. An `Origin` header does not change this. |
| No `Sec-Fetch-Site` header, and an `Origin` header with the host and the port of the request | Accepts it. The scheme is not compared. |
| No `Sec-Fetch-Site` header, and an `Origin` header with another host or port | Refuses it with `403`. |
| An agent token in the `Authorization` header | Does not check it. |

`curl` sends neither header. So the examples on this page need no extra header, and this command creates a project:

```sh
curl -sS -b vellum.cookies -H 'content-type: application/json' \
  -d '{"id": "00000000-0000-4000-8000-000000000101", "name": "API example"}' \
  -w '\n%{http_code}\n' "$VELLUM_URL/api/workspaces/$WORKSPACE/projects"
```

Do not copy the headers of a browser into a script. If your tool adds an `Origin` header, make its value the address of your instance:

```sh
curl -sS -b vellum.cookies -H 'content-type: application/json' -H "Origin: $VELLUM_URL" \
  -d '{"id": "00000000-0000-4000-8000-000000000102", "name": "API example"}' \
  -w '\n%{http_code}\n' "$VELLUM_URL/api/workspaces/$WORKSPACE/projects"
```

Behind a reverse proxy, the `Origin` value must have the host name that Vellum sees in the `Host` header. That is the public host name when the proxy passes `Host` on unchanged.

A refused request gets this answer, and Vellum changes nothing:

```text
{"type":"cross_site_request","title":"The request does not come from this app","detail":"A page of another origin sent it. Open Vellum and do it there.","errors":null}
403
```

## Read an error

An error has a JSON body with a `type`, a `title` and, for some errors, a `detail` or a list `errors` with one entry for each field.

Send the same element a second time:

<!-- snippet: live -->
```sh
curl -sS -b vellum.cookies -H 'content-type: application/json' -d @element.json -w '\n%{http_code}\n' "$VELLUM_URL/api/projects/$PROJECT/elements"
```

```text
{"type":"conflict","title":"ID <the ID> is already used by an element","detail":null,"errors":null}
409
```

Send an element with no name:

<!-- snippet: live -->
```sh
curl -sS -b vellum.cookies -H 'content-type: application/json' -d '{"id": "00000000-0000-4000-8000-000000000001", "kind": "system", "name": "", "tags": []}' \
  -w '\n%{http_code}\n' "$VELLUM_URL/api/projects/$PROJECT/elements"
```

```text
{"type":"validation_error","title":"Validation failed","detail":null,"errors":[{"field":"name","message":"Name is required"}]}
400
```

Read the `type` in a script, not the `title`. [Conflicts and refused requests](/troubleshooting/conflicts/) lists the common types and the fix for each.

| Status | Meaning |
|---|---|
| `400` | The request is not valid. `errors` names each field. |
| `401` | No session, or the session ended. Sign in again. |
| `403` | The account has no access to the workspace, the role is too low, or an agent token called an endpoint for people. With the type `cross_site_request`, a write has a `Sec-Fetch-Site` or an `Origin` header that names another origin. |
| `404` | The thing does not exist. |
| `409` | The request conflicts with the stored state. Read the state again, then decide. |
| `413` | The body is larger than the limit of the endpoint. |
| `428` | A settings write has no `If-Match` header. |
| `429` | Too many calls. Wait for the time in `Retry-After`. |

## Page through a list

Most lists answer with `items` and a `cursor`. Send the cursor back to get the next page. A `cursor` of `null` means that the list has no more items.

<!-- snippet: live -->
```sh
CURSOR=""
while :; do
  PAGE=$(curl -sS --fail-with-body -b vellum.cookies "$VELLUM_URL/api/projects/$PROJECT/elements?limit=50${CURSOR:+&cursor=$CURSOR}")
  echo "$PAGE" | jq -r '.items[] | "\(.kind)  \(.name)"'
  CURSOR=$(echo "$PAGE" | jq -r '.cursor // empty | @uri')
  [ -n "$CURSOR" ] || break
done
```

A cursor that the list did not give gets `400` on the field `cursor`. A few lists have no pages and answer with a plain array, for example `GET /api/workspaces`. The reference shows the answer type of each list.

## Send a settings version

The workspace settings and the instance settings have a version. A write must name the version that you read.

1. Read the settings. The version is in the field `version` and in the `ETag` header.
2. Change the `overrides` document.
3. Send it with the `ETag` value in the `If-Match` header.

<!-- snippet: live -->
```sh
curl -sS --fail-with-body -b vellum.cookies -D settings.headers -o settings.json "$VELLUM_URL/api/workspaces/$WORKSPACE/settings"
ETAG=$(grep -i '^etag:' settings.headers | tr -d '\r' | cut -d' ' -f2)
jq '.overrides | .github = ((.github // {}) + {defaultRef: "main"})' settings.json |
  curl -sS --fail-with-body -b vellum.cookies -X PUT -H 'content-type: application/json' -H "If-Match: $ETAG" -d @- \
    "$VELLUM_URL/api/workspaces/$WORKSPACE/settings" | jq -c '{version, defaultRef: .effective.github.defaultRef}'
```

If another person saved after you read, Vellum answers `409` and saves nothing. Read the settings again and apply your change to the new document. A write with no `If-Match` header gets `428`. [Settings](/reference/settings/) lists every key.

A promise approval works in the same way with a content hash: `POST /api/projects/{projectId}/promises/{promiseId}/approve` names the revision and the `contentHash` that you read.

## Work on a draft or a variant

The endpoints of the model, the contracts, the scenarios and the cost sheet read and write the main model. Many of them take the query parameter `branchId` to work on a draft or a variant in place of the main model. The reference shows the parameter on each endpoint that takes it. See [Work in a draft](/how-to/work-in-a-draft/) and [Save revisions and compare variants](/how-to/compare-variants/).

## Get the OpenAPI document

The repository holds the OpenAPI document of the API as `src/Vellum.Web/openapi.json`. The build of the API writes it, and the reference pages of this site are generated from it. 

An instance serves the document at `/openapi/v1.json` only when it runs in the `Development` environment, as in `dotnet run`. The container image does not serve it.

## Limits

- The API has no version in its path. Before version 1.0, a minor release can change an endpoint. The release notes list each change.
- The OpenAPI document has no description for an endpoint or a field. The how-to and reference pages of each feature give the meaning.
- The paths `/health`, `/health/live`, `/health/ready`, `/mcp`, `/llms.txt`, `/llms-full.txt` and `/skills/vellum/SKILL.md` are not in the OpenAPI document. The path `/api/auth/external/callback` is not in it either: the sign-in with GitHub answers it, and only GitHub sends a browser there.
- A caller with no valid credential gets 300 calls in one minute from one address to `/api`, `/mcp`, `/llms.txt`, `/llms-full.txt` and `/skills` together. After that the answer is `429`. A call with a valid session cookie or a valid agent token does not count.
- Each answer of `/api` and `/mcp` has the header `Cache-Control: no-store`.

## See also

- [HTTP API](/reference/http-api/)
- [Connect a coding agent](/how-to/connect-a-coding-agent/)
- [MCP tools](/reference/mcp-tools/)
- [The promise loop: from a question to evidence](/tutorials/promise-loop/) uses the API for a complete flow.
