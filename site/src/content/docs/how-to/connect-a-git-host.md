---
title: Connect GitLab, Bitbucket or Azure DevOps
description: Read repositories on GitLab, Bitbucket Cloud or Azure DevOps Repos with a read-only token, and scan them as Sources.
---

This guide shows you how to connect a workspace to GitLab, Bitbucket Cloud or Azure DevOps Repos, and how to scan a repository there. For GitHub, see [Connect GitHub](/how-to/connect-github/).

To set the address and the token, you need the owner role in the workspace. To add a Source and scan it, you need the editor role.

Vellum reads each host through its REST API. It does not use the git protocol and runs no git command. It never writes to the host.

## What each host needs

| Host | Setting | Default | Secret | Repository form |
|---|---|---|---|---|
| GitLab | `gitlab.apiBaseUrl` | `https://gitlab.com/api/v4` | `gitlab:token` | `group/name`, or `group/subgroup/name` with up to 18 subgroups |
| Bitbucket Cloud | `bitbucket.apiBaseUrl` | `https://api.bitbucket.org/2.0` | `bitbucket:token` | `workspace/repository` |
| Azure DevOps Repos | `azureDevops.apiBaseUrl` | `https://dev.azure.com` | `azure-devops:token` | `organization/project/repository` |

You can also give a repository as the address of its page on the host. The address must be on the host of the setting. Vellum removes a `.git` at the end. It reads a GitLab address up to `/-/`, and an Azure DevOps address with `_git` in it.

An empty setting turns the host off. A Source of that host then cannot scan, and Vellum refuses a scan with `gitlab_not_configured`, `bitbucket_not_configured` or `azure_devops_not_configured`.

## Set the address of the host

1. Open **Settings**, then **GitLab**, **Bitbucket** or **Azure DevOps**.
2. Leave **API base URL** at the default for gitlab.com, Bitbucket Cloud and Azure DevOps Services. For a self-managed GitLab, give the API address of your server, for example `https://gitlab.example.com/api/v4`.
3. Save the change.

If you change the address after you store a token, store the token again. A token is bound to the origin of the address.

The address must obey the [URL rules](/reference/settings/#url-rules). The rules are the same as for the GitHub address. In the hosted profile the address must use `https`. When the instance policy refuses private network URLs, Vellum refuses an address that resolves to a private address.

## Store a read-only token

A public repository needs no token. A private repository needs one.

### GitLab

1. On GitLab, create a personal, group or project access token.
2. Give it the scope `read_api` or `read_repository`. Give it no write scope.
3. Store it as `gitlab:token`.

Vellum sends it in the header `PRIVATE-TOKEN`.

### Bitbucket Cloud

1. On Bitbucket, create an access token with read access to repositories. An access token of a workspace, a project or a repository works.
2. Store it as `bitbucket:token`. Vellum sends it as a bearer token.

To use an API token or an app password in its place, store it as `user:token`, with your Bitbucket user name before the colon. Vellum then sends it with HTTP basic authentication. A value with a colon is always sent this way.

### Azure DevOps

1. In Azure DevOps, create a personal access token for the organization.
2. Give it the scope **Code (Read)** and no other scope.
3. Store it as `azure-devops:token`.

Vellum sends it with HTTP basic authentication and an empty user name.

### Store the value

1. Open **Settings**, then the section of the host.
2. Select **Set** next to the token, paste the token and select **Save**.

With the HTTP API, `PUT /api/workspaces/{workspaceId}/secrets/{name}` stores it. The name is `gitlab:token`, `bitbucket:token` or `azure-devops:token`. See [Use the HTTP API](/how-to/use-the-http-api/#sign-in) for the cookie file and the variables.

```sh
jq -n --arg value "$TOKEN" '{value: $value}' |
  curl -sS --fail-with-body -b vellum.cookies -X PUT -H 'content-type: application/json' -d @- \
    "$VELLUM_URL/api/workspaces/$WORKSPACE/secrets/gitlab:token"
```

Vellum encrypts the token and does not show it again. The token is bound to the origin of the API address of its host. Vellum sends it to that origin only. If you change the address, Vellum does not send the token. Store it again.

A token goes in an HTTP header. So Vellum accepts only printable ASCII. See [Secret names](/reference/settings/#secret-names) and [Security model](/concepts/security-model/#origin-binding).

## Test the connection

In the section of the host, select **Test connection**. Save your changes first. The result names the account of the token. Without a token, only public repositories are reachable.

## Add a Source and scan it

1. Open the project and select **Import from GitHub** in the left toolbar.
2. Under **Sources**, select **Add Source**.
3. Select **GitLab**, **Bitbucket** or **Azure DevOps**. A host with no usable address shows **Not available** and the reason.
4. In **Repository**, give the repository in the form of the host, or the address of its page.
5. In **Branch or tag**, give a branch, a tag or a full commit SHA. Leave it empty to read the default branch of the repository.
6. Optional: in **Name**, give the Source another name than the repository.
7. Select **Add and scan**.

A project has one Source for each repository. A second Source for the same repository is refused with the code `source_exists`.

### With the HTTP API

The examples need the cookie file, `$VELLUM_URL` and `$PROJECT` from [Use the HTTP API](/how-to/use-the-http-api/#sign-in).

1. Create the Source. Give the kind and the repository:

   ```sh
   SOURCE=$(jq -n '{kind: "gitlab", repository: "acme/platform/orders"}' |
     curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @- \
       "$VELLUM_URL/api/projects/$PROJECT/sources" | jq -r '.id')
   ```

   The kind is `gitlab`, `bitbucket` or `azure-devops`. Add `name` to give the Source another name. A second Source for the same repository gets `409`.
2. Start a scan:

   ```sh
   curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d '{"ref": "main"}' \
     "$VELLUM_URL/api/projects/$PROJECT/sources/$SOURCE/scans"
   ```

   `ref` is a branch, a tag or a full commit SHA. Leave it out to read the default branch of the repository. `includePaths` and `excludePaths` limit the scan to some folders, as for GitHub.

## What the scan does

To scan the Source again at its default ref, select **Scan again** on the Source.

The scan reads like a GitHub scan. Vellum finds the commit that the ref points to, lists the files of that commit and reads the files in the scope. It applies the same [limits](/how-to/import-a-repository/#limits), the same exclusions and the same secret filter. You review the proposals in the same way. See [Import a repository](/how-to/import-a-repository/#watch-the-scan).

A scan of a pull request reads GitHub only. On another host Vellum refuses it with `pull_request_github_only`. Scan a branch, a tag or a commit.

To add many repositories of one group, workspace or organization, see [Add many repositories](/how-to/add-many-repositories/).

## When you change the address

A Source keeps the host it was created on. If you set the address of the host to another server, Vellum refuses a scan of the Source with `409` and the code `source_host_changed`. Set the address back, or create a Source on the new host.

## See also

- [Connect GitHub](/how-to/connect-github/)
- [Import a repository](/how-to/import-a-repository/)
- [Add many repositories](/how-to/add-many-repositories/)
- [Settings](/reference/settings/#git-hosts)
- [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/)
