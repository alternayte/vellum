---
title: Add many repositories
description: List the repositories of an organization, group or workspace, add up to 100 of them as Sources in one request, and scan each one.
---

This guide shows you how to add many repositories of one owner to a project in one step. Vellum creates one Source for each repository and starts one scan of each. You need the editor role, signed in as a person.

Bulk add reads GitHub, GitLab, Bitbucket Cloud and Azure DevOps. Connect the host first. See [Connect GitHub](/how-to/connect-github/) or [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/).

## Add the repositories

1. Open the project and select **Import from GitHub** in the left toolbar.
2. Under **Sources**, select **Add many repositories**.
3. In **Host**, select the git host. A host with no usable address shows **(not set up)** and the reason.
4. Give the owner: the organization or user on GitHub, the group on GitLab, the workspace on Bitbucket, or the organization or `organization/project` on Azure DevOps.
5. Select **List repositories**. The list shows one page. Select **Next** and **Previous** to move between pages.
6. Select the repositories. **Select all on this page** selects each repository on the page that has no Source. A repository with **Has a Source** is a Source of the project already. If you select it, Vellum starts a new scan of that Source.
7. Optional: in **Branch or tag for every scan**, give the ref that every scan reads. Leave it empty to read the default branch of each repository.
8. Select **Add N and scan**. You can select at most 100 repositories at a time.

The result has one line for each repository: **Added**, **Already a Source** or **Not added**, and **Scan queued** or **No scan** with the reason.

Without a token, the list holds public repositories only.

## With the HTTP API

The examples need the cookie file, `$VELLUM_URL` and `$PROJECT` from [Use the HTTP API](/how-to/use-the-http-api/#sign-in).

### List the repositories of an owner

`GET /api/projects/{projectId}/sources/repositories?kind=&owner=&page=` returns one page of the repositories of an owner.

| `kind` | `owner` |
|---|---|
| `github` | An organization or a user, such as `acme` |
| `gitlab` | A group, or a group path such as `acme/platform` |
| `bitbucket` | A workspace |
| `azure-devops` | An organization, or `organization/project` |

`page` counts from 1. The largest value is 100.

```sh
curl -sS --fail-with-body -b vellum.cookies \
  "$VELLUM_URL/api/projects/$PROJECT/sources/repositories?kind=gitlab&owner=acme/platform&page=1" |
  jq -r '.repositories[] | [.repository, .defaultBranch, .isPrivate, .sourceId] | @tsv'
```

Each entry has the repository in the form that a Source keeps, its default branch, whether it is private, and `sourceId` when the project has a Source for it already. `hasMore` is `true` when the host has another page.

An owner that the host does not show gets `404`.

### Add the repositories

Send the repositories to `POST /api/projects/{projectId}/sources/bulk`:

```sh
jq -n '{kind: "gitlab", repositories: ["acme/platform/orders", "acme/platform/billing"], ref: "main"}' |
  curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @- \
    "$VELLUM_URL/api/projects/$PROJECT/sources/bulk" |
  jq -r '.items[] | [.repository, .created, .scanId, .error.type] | @tsv'
```

- `repositories` holds 1 to 100 repositories, in the form of the host. See [What each host needs](/how-to/connect-a-git-host/#what-each-host-needs). Vellum ignores a repository that the list repeats.
- `ref` is optional. Every scan reads that branch, tag or commit. Without it, each scan reads the default ref of the workspace, or the default branch of its repository.

The answer has one item for each repository:

| Field | Meaning |
|---|---|
| `repository` | The repository. |
| `sourceId` | The Source. Null when Vellum could not add it. |
| `created` | `true` when this request created the Source. `false` when the project had it already. Vellum then starts a new scan of the existing Source. |
| `scanId` | The scan that this request queued. Null when Vellum queued no scan. |
| `error` | Why Vellum did not add the repository or did not queue its scan, with `type` and `title`. Null when it did both. |

## When one repository fails

Each repository is a separate step. One failure does not stop the others. Examples of a failure are a name in the wrong form, the daily scan limit (`scan_limit_reached`) and a scan of the same Source that runs (`scan_in_progress`).

Each scan counts for **Repository scans per day**. When you add more repositories than the limit allows, Vellum queues scans until the limit is reached. The other repositories get `scan_limit_reached`.

## Watch the scans

The scans wait in the one scan queue of the instance. Vellum runs at most 2 scans for one git host at the same time in one workspace. The other scans of that host in the workspace wait. An operator changes the number with `Import__Worker__MaxRunningPerHost`. See [Bootstrap environment](/reference/bootstrap-environment/#scan-worker).

Each Source shows its own progress and its own failure. A scan that a limit stopped is partial. Vellum never shows a partial scan as complete. Resume or rescan each failed Source by itself. See [Watch the scan](/how-to/import-a-repository/#watch-the-scan).

Review the proposals of each Source by itself. [Review by Source](/how-to/import-a-repository/#review-by-source) lists the pending proposals of all Sources, one group for each Source.

## See also

- [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/)
- [Import a repository](/how-to/import-a-repository/)
- [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/)
