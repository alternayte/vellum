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

## GitHub Enterprise Server

Set **API base URL** to the API address of your server, for example `https://github.example.com/api/v3`. Save the change, then enter the token again.

## Test the connection

Select **Test connection**. The result names the account of the token and the requests that remain in this hour. Without a token it says that only public repositories are reachable.

## What Vellum requests

Vellum asks GitHub for the repository, the commit a branch or tag points to, the file tree of that commit, the files it reads, the comparison of two commits, and a pull request. It follows a redirect only to the same host.
