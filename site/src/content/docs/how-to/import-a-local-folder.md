---
title: Import a local folder
description: Scan a folder on the machine that runs a local Vellum instance, as a directory Source.
---

This guide shows you how to scan a folder on the machine that runs Vellum. A folder is a `directory` Source. You need the editor role.

A directory Source works in the local profile only. A hosted instance reads no folder of its own machine. It refuses the kind with the code `source_kind_not_in_profile`. Upload an archive or connect a git host there.

## Give the instance a sources root

The instance reads folders under one root folder only: the sources root. Without a root, Vellum refuses a directory Source with the code `sources_root_not_configured`.

### With Docker Compose

`docker-compose.yml` mounts one folder of your machine at `/sources` in the container, read-only, and sets `Vellum__SourcesRoot` to `/sources`. The folder is `VELLUM_SOURCES`. The default is `./sources` next to the Compose file.

1. Put the folders that you want to scan under one folder, for example `~/code`.
2. Start Vellum with that folder:

   ```sh
   VELLUM_SOURCES="$HOME/code" docker compose up -d
   ```

   You can also set `VELLUM_SOURCES` in the `.env` file.

A change of the folder needs a restart of the container.

### With a server that runs on the host

Set `Vellum__SourcesRoot` to the root folder, and start Vellum:

```sh
Vellum__SourcesRoot="$HOME/code" dotnet Vellum.dll
```

A change of the value needs a restart. If the value does not name a folder that the server can read, Vellum refuses a directory Source with the code `sources_root_unreadable`.

`GET /api/projects/{projectId}/sources/kinds` shows the real path of the root in `where` of the `directory` kind, or the reason when the kind is not available.

## Add the Source and scan it

1. Open the project and select **Sources** in the sidebar.
2. Under **Sources**, select **Add Source**.
3. Select **Folder**. The field shows the sources root before the path. If **Folder** shows **Not available**, the dialog gives the reason.
4. In **Folder**, give the folder as a path relative to the root, with `/` between its parts, for example `acme/orders`.
5. Optional: in **Name**, give the Source another name than the path.
6. Select **Add and scan**.

Vellum refuses these paths:

- An absolute path, a path with a backslash or a path with a drive letter.
- A path with a `..`, a `.` or an empty part.
- A path that does not name a folder under the root. The code is `directory_refused`.
- A path that leaves the root through a symbolic link. The code is `directory_refused`.

The scan reads the folder as it is at that time. Vellum does not watch the folder. To see a change, select **Read again** on the Source. Each scan gives a Snapshot. Its ID is the identity of the scan. Two scans of the same files give the same ID.

### With the HTTP API

The examples need the cookie file, `$VELLUM_URL` and `$PROJECT` from [Use the HTTP API](/how-to/use-the-http-api/#sign-in).

```sh
SOURCE=$(jq -n '{kind: "directory", path: "acme/orders"}' |
  curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @- \
    "$VELLUM_URL/api/projects/$PROJECT/sources" | jq -r '.id')
curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d '{}' \
  "$VELLUM_URL/api/projects/$PROJECT/sources/$SOURCE/scans"
```

A folder has no refs. Leave `ref` out. Vellum refuses a scan with a ref.

The scan applies the same [limits](/how-to/import-a-repository/#limits), the same exclusions and the same secret filter as a repository scan. You review the proposals in the same way. See [Import a repository](/how-to/import-a-repository/#review-the-proposals).

## What Vellum reads

- **The root is the boundary.** On each scan, Vellum resolves the real path of the folder and of each file. A path or a symbolic link that leaves the root is refused. This applies to a link to a file and to a link to a folder.
- **Links in the folder are not followed.** Vellum reads a link as git stores it: a file whose content is the text of its target.
- **No `.git` folder.** Vellum does not list `.git`. It also leaves out each entry that is not a file, a folder or a link, such as a pipe or a device.
- **The commit is information only.** When the folder has a `.git` folder, Vellum reads `.git/HEAD` as text, and the ref file or the `packed-refs` line that it names. The Snapshot shows that commit SHA. Vellum does not check the files against it. No git command runs.
- **Nothing is written and nothing runs.** Compose mounts the root read-only. Vellum only reads files.

A folder with more than 200,000 entries, or with more than 64 levels of folders, fails the scan with `directory_too_large` or `directory_too_deep`. Choose a smaller folder.

A file that disappears during the scan makes the scan partial. A file that becomes a link or a pipe before Vellum opens it is not read.

A folder that the server cannot list also makes the scan partial. The scan shows the note `folder-unreadable` with the path of that folder. Vellum proposes no removal and marks no fact stale for anything under that folder. Fix the permissions, then scan again.

## Cite a folder

A fact from a folder cites `directory/<path>@<snapshot-id>:path:start-end`. `<path>` is the folder under the root. See [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/#a-citation-names-the-source-and-the-revision).

## See also

- [Upload an archive](/how-to/upload-an-archive/)
- [Bootstrap environment](/reference/bootstrap-environment/)
- [Security model](/concepts/security-model/#imported-files)
- [Local and hosted profiles](/concepts/local-and-hosted-profiles/)
