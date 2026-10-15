---
title: Upload an archive
description: Scan a .zip or .tar.gz file of source code as an archive Source, and know which archives Vellum refuses.
---

This guide shows you how to scan source code that you upload as one `.zip` or `.tar.gz` file. An uploaded file is an `archive` Source. You need the editor role, signed in as a person. An agent token cannot upload.

An archive Source works in the local profile and in the hosted profile.

## Add the Source and upload the file

1. Open the project and select **Import from GitHub** in the left toolbar.
2. Under **Sources**, select **Add Source**.
3. Select **Archive**.
4. In **Name**, give the Source a name, for example `orders-service`. Its citations carry the name.
5. In **File**, choose the `.zip` or `.tar.gz` file. The field shows the size limit of the workspace.
6. Select **Add and upload**.

The dialog shows the upload progress. Then the server checks each entry of the archive and queues the scan.

If Vellum refuses the file, the Source stays. Choose another file and select **Upload**.

The citation uses the name in lower case, with a dash in place of each run of other characters. The name must have one letter or digit or more. A project has one archive Source for each such name.

Each upload is a new scan and a new Snapshot. To scan a newer version of the code, select **Upload again** on the Source and choose the file.

### With the HTTP API

The examples need the cookie file, `$VELLUM_URL` and `$PROJECT` from [Use the HTTP API](/how-to/use-the-http-api/#sign-in). Create the Source, then send the file as the raw body of `POST /api/projects/{projectId}/sources/{sourceId}/archive`:

```sh
SOURCE=$(jq -n '{kind: "archive", name: "orders-service"}' |
  curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @- \
    "$VELLUM_URL/api/projects/$PROJECT/sources" | jq -r '.id')
curl -sS --fail-with-body -b vellum.cookies --data-binary @orders-service.zip \
  "$VELLUM_URL/api/projects/$PROJECT/sources/$SOURCE/archive"
```

The answer is `202` with the scan. `POST /api/projects/{projectId}/sources/{sourceId}/scans` does not scan an archive Source. It answers `archive_needs_upload`.

## Format and scan limits

Vellum knows the format from the first bytes of the file, not from the name. It reads `.zip` and `.tar.gz` only. Any other file gets `400` with the code `archive_format`.

An upload counts as one scan for **Repository scans per day**. A project runs one scan of a Source at a time. A second upload while a scan of the Source runs gets `409` with the code `scan_in_progress`.

## What Vellum does with the file

1. Vellum writes the body to a temporary file in the data folder. It deletes the file when the request ends. It does not keep the archive.
2. It reads each entry from that file. It extracts no entry to disk and runs nothing.
3. It checks every entry against the refusal rules below. One failed rule refuses the whole archive. Vellum then stores nothing.
4. If all files are in one top folder, Vellum removes that folder from each path.
5. It selects the files with the scan rules: the [limits](/how-to/import-a-repository/#limits), the excluded paths, and the exclusion of binary files, vendored files and secrets. The limits are **Files per import**, **Largest file** and **Total size**. A file that a rule leaves out does not refuse the archive.
6. It stores the cleared content of the selected files only.

## Why Vellum refuses an archive

| Code | Status | The archive |
|---|---|---|
| `archive_format` | `400` | is not a `.zip` or a `.tar.gz` file. |
| `archive_unreadable` | `400` | is damaged or encrypted, an entry does not have the size that its header states, the compressed sizes of a zip add up to more than the file, or a tar metadata entry (a PAX header or a long name) is larger than 64 KiB. |
| `archive_absolute_path` | `400` | has an entry with an absolute path, a backslash or a drive letter. |
| `archive_path_traversal` | `400` | has an entry with a `..`, a `.` or an empty part in its path, or a control character. |
| `archive_link_entry` | `400` | has a symbolic link, a hard link, a device or another entry that is not a file or a folder. |
| `archive_nested` | `400` | has another archive in it. Vellum knows it by the name, such as `.zip`, `.jar`, `.tar`, `.gz` or `.7z`, and by the first bytes of the content. |
| `archive_duplicate_path` | `400` | has two entries with the same path. |
| `archive_compression_ratio` | `400` | has an entry larger than 64 KiB that expands to more than 100 times its compressed size. |
| `archive_too_many_entries` | `400` | has more than 200,000 entries. |
| `archive_too_large` | `400` | expands to more than `import.maxArchiveBytes`, all files together. |
| `archive_too_large` | `413` | is an upload larger than `import.maxArchiveBytes` and 64 MiB. |
| `archive_upload_busy` | `429` | arrives while the workspace has 2 uploads in progress. Upload it again when one ends. |
| `archive_no_space` | `503` | arrives while the server has less free disk space than the upload can take. |

The server writes the upload to a temporary file in its data folder and deletes it when the request ends. The next upload deletes a temporary file that is older than one hour, for example after a crash.

Vellum counts the bytes of an entry as they expand. It does not trust the size that a header states.

The message of a refusal names no entry. An entry name is untrusted text.

To fix a refusal, pack the files again:

- Use paths relative to the archive.
- Replace each link with the file that it points to.
- Unpack an inner archive first.

## The size limit

`import.maxArchiveBytes` is the largest total size of the files of one archive, after expansion. The default is 256 MiB (268,435,456 bytes). A workspace owner sets 1 MiB to 4 GiB under **Settings → Import limits → Uploaded archive**. See [Settings](/reference/settings/#import).

`GET /api/projects/{projectId}/sources/kinds` returns the limit of the workspace in `maxArchiveBytes`.

## Cite an archive

A fact from an archive cites `archive/<name>@<snapshot-id>:path:start-end`. The revision is the Snapshot ID, because an archive has no commit. See [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/#a-citation-names-the-source-and-the-revision).

## See also

- [Import a local folder](/how-to/import-a-local-folder/)
- [Import a repository](/how-to/import-a-repository/)
- [Security model](/concepts/security-model/#imported-files)
