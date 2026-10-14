---
title: Connect GitHub
description: Read public repositories without a token, and private ones with a read-only token.
---

This guide shows you how to connect a workspace to GitHub. You need the owner role in the workspace.

Vellum reads repositories. It never writes to them.

## Public repositories

A public repository needs no token. GitHub allows a small number of requests each hour without one, so a large scan can pause until the limit resets.

## Private repositories

1. On GitHub, create a fine-grained personal access token. Select only the repositories Vellum must read. Give it read-only access to **Contents** and **Metadata**.
2. In Vellum, open **Settings**, then **GitHub**.
3. Select **Set** next to the access token, paste the token and select **Save**.

Vellum encrypts the token and does not show it again. The token is bound to the host of the API base URL. Vellum sends it to that host only.

## Use your gh login on a local instance

On a local instance, Vellum can read GitHub with the token of the GitHub CLI. You need `gh`, logged in with `gh auth login`.

1. Start Vellum with your gh token:

   ```sh
   just up-gh
   ```

   This runs `GH_TOKEN=$(gh auth token) docker compose up -d --build`. It stops with a message when `gh` is missing or not logged in.
2. In Vellum, open **Settings**, then **GitHub**. The page shows **GitHub: your gh login (your-login), from GH_TOKEN.**

These rules apply:

- Vellum uses `GH_TOKEN` only for a workspace that has no access token of its own. A workspace token always wins.
- Vellum uses `GH_TOKEN` only for workspaces where the instance admin is an Owner. Another user's workspace reads anonymously or with its own token.
- Vellum sends `GH_TOKEN` to `https://api.github.com` only, never to a GitHub Enterprise Server.
- Vellum reads `GH_TOKEN` once at start. To use a new gh login, restart Vellum.
- Vellum does not store `GH_TOKEN`. It reads `GH_TOKEN` only, never `GITHUB_TOKEN`.
- A hosted instance ignores `GH_TOKEN`. The instance diagnostics show the state.

To run without the gh login, start with `just up-all` or `docker compose up -d`.

## GitHub Enterprise Server

Set **API base URL** to the API address of your server, for example `https://github.example.com/api/v3`. Save the change, then enter the token again.

## Test the connection

Select **Test connection**. The result names the account of the token and the requests that remain in this hour. Without a token it says that only public repositories are reachable.

## What Vellum requests

Vellum asks GitHub for the repository, the commit a branch or tag points to, the file tree of that commit, the files it reads, the comparison of two commits, and a pull request. It follows a redirect only to the same host.
