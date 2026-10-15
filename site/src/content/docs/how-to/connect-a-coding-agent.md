---
title: Connect a coding agent
description: Create an agent token, give it to an MCP client, and check what the token can do.
---

This guide shows you how to connect a coding agent, such as Claude Code or Cursor, to a workspace. You need the owner role in the workspace.

An agent reads the model, runs the review, exports a specification, and records evidence. It proposes draft promises, decisions, questions, additions to the model and flows. It cannot approve a promise, accept a decision or a proposal, or change settings. See [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

## Create a token

1. Open the workspace **Settings**, then **Agent tokens**.
2. Enter a name that says which agent uses the token.
3. Below **The token can**, select the scopes.
   - `read`: read the model, the contracts, the scenarios, the promises, the decisions and the open questions, and run the review.
   - `propose`: write draft promises and obligations, proposed decisions, questions and draft proposals. Run checks. Record evidence.
   - `export`: export the SDD, the bundle and the brief, and check a bundle.
   - `scan-pull-request`: start a scan of a pull request, for CI. See [Report impact in pull request CI](/how-to/report-impact-in-pull-request-ci/).
   - `push`: push evidence with `vellum scan`. See [Push evidence from CI or a laptop](/how-to/push-evidence-from-ci-or-a-laptop/).
4. Select **Create token**.
5. Select **Copy**. Vellum shows the token one time only.

![The Agent tokens section of the workspace settings. The form has a Name and the checkboxes Read, Propose, Export and Scan pull requests below The token can. The table below lists the token Claude Code on my laptop that can read, propose and export, was never used, expires on 12/01/2027 and has the button Revoke.](../../../assets/screenshots/settings-agent-tokens.png)

Give an agent only the scopes its task needs. A token that only reads cannot write a draft.

A token that you create in the app works on every project of the workspace and expires after 90 days.

## Restrict a token to one project

The API creates a token for one project, or with another expiry. Send the request from a signed-in session of an owner. The longest life is 365 days.

```http
POST /api/workspaces/{workspaceId}/agent-tokens

{ "name": "Orders agent", "scopes": ["read", "export"], "projectId": "<project ID>", "expiresAt": "2027-01-31T00:00:00Z" }
```

The answer holds the token. No later request shows it again.

## Configure the MCP client

The MCP endpoint is `/mcp` on your Vellum address. It uses streamable HTTP. The client sends the token in the `Authorization` header.

The endpoint keeps no session and opens no stream. Each message is one POST. A GET or a DELETE of `/mcp` with a token gets 405 with `Allow: POST`. A client reads that as "this server has no stream" and continues.

For Claude Code:

```sh
claude mcp add --transport http vellum https://vellum.example.com/mcp \
  --header "Authorization: Bearer vlm_agt_..."
```

For a client that reads a JSON configuration:

```json
{
  "mcpServers": {
    "vellum": {
      "type": "http",
      "url": "https://vellum.example.com/mcp",
      "headers": {
        "Authorization": "Bearer vlm_agt_..."
      }
    }
  }
}
```

Replace the address with your own. Keep the token out of a file that you commit. Most clients can read the header value from an environment variable.

## Check the connection with the MCP Inspector

Vellum is checked against the command-line mode of the MCP Inspector, version 2.10.1. It is a real client of the streamable HTTP transport. The two snippets above give the same three values to other clients: the transport `http`, the address, and the `Authorization` header.

List the tools:

```sh
npx -y @modelcontextprotocol/inspector --cli https://vellum.example.com/mcp \
  --transport http \
  --header "Authorization: Bearer vlm_agt_..." \
  --method tools/list
```

The answer is `{ "tools": [ … ] }` with 32 tools. An empty list means that the client did not reach the endpoint: check the address and the header.

Call a tool:

```sh
npx -y @modelcontextprotocol/inspector --cli https://vellum.example.com/mcp \
  --transport http \
  --header "Authorization: Bearer vlm_agt_..." \
  --method tools/call --tool-name get_model_summary \
  --tool-arg projectId=<project-id>
```

The answer holds one text block. The text is a JSON document with `projectId`, `truth`, `truthNote` and `data`.

## Check the token

Send the token to the capability endpoint.

```sh
curl -H "Authorization: Bearer vlm_agt_..." https://vellum.example.com/api/agent/capabilities
```

The answer has the scopes of the token, its project restriction and its expiry. `tools` lists the MCP tools the token can call. `endpoints` lists the HTTP endpoints the token can call.

An answer of 401 means that the token is wrong, revoked or expired.

## Install the skill

The skill tells an agent when to use Vellum and which tool to use. Your Vellum address serves it at `/skills/vellum/SKILL.md`.

```sh
mkdir -p .claude/skills/vellum
curl -o .claude/skills/vellum/SKILL.md https://vellum.example.com/skills/vellum/SKILL.md
```

An agent that reads `/llms.txt` on your Vellum address finds the skill and the full reference by itself.

## Revoke a token

1. Open the workspace **Settings**, then **Agent tokens**.
2. Select **Revoke** next to the token.

The token stops at the next request. The MCP endpoint keeps no session, so an agent that is connected stops at its next message.

A token also stops when the user who created it leaves the workspace. A token has no more access than that user has.

## Use the HTTP API without MCP

An agent that has no MCP client sends the same token to the HTTP API. Most tools call one HTTP endpoint. [MCP tools](/reference/mcp-tools/) names that endpoint in the line `HTTP` of the tool.

Some tools read the model and put more than one list in a result: `get_element`, `get_dependencies`, `get_dependents`, `list_messages`, `get_contracts` and `get_model_summary`. No single endpoint gives their result. The reference says so for each. The lists of the HTTP API give the same facts in separate requests. `/llms-full.txt` lists every HTTP endpoint that a token can call.
