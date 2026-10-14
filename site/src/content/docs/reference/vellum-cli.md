---
title: vellum CLI
description: The commands, flags, environment variables and exit codes of vellum scan.
---

`vellum` is one self-contained file. `just cli` builds it. It runs the extractors of the Vellum server it was built with, on the machine where it runs, and pushes the result to a project. See [Push evidence from CI or a laptop](/how-to/push-evidence-from-ci-or-a-laptop/).

## Commands

| Command | What it does |
|---|---|
| `vellum scan <dir>... [flags]` | Scans each directory and pushes it as a Snapshot of an `agent-push` Source. One directory is one Source. |
| `vellum --version` | Prints the version. |
| `vellum --help` | Prints the commands and the flags. |

## Flags of `vellum scan`

| Flag | Value | Default | Meaning |
|---|---|---|---|
| `--url` | URL | `$VELLUM_URL` | The address of the Vellum app. `http` is refused for a host that is not this machine. |
| `--project` | ID | `$VELLUM_PROJECT` | The project that gets the push. |
| `--name` | `<name>` or `<dir>=<name>` | The folder name | The Source name. With one directory, `<name>` is enough. With several, write `<dir>=<name>` as the directory is given. A Source is found by the slug of its name. |
| `--include` | path | All | Scan only under this path, relative to the directory. Repeat for more paths. |
| `--exclude` | path | None | Leave out this path. Repeat for more paths. |
| `--exclude-glob` | glob | None | Leave out each path that matches, in addition to `import.excludeGlobs`. Repeat for more globs. |
| `--max-files` | number | `import.maxFiles` | Read at most this many files. |
| `--max-file-bytes` | number | `import.maxFileBytes` | Read no file larger than this. |
| `--max-total-bytes` | number | `import.maxTotalBytes` | Read at most this many bytes in all. |
| `--dry-run` | | Off | Scan and print the counts and the Snapshot ID. Push nothing. Without `--url`, `--project` and a token, the default import limits apply. |
| `--payload` | file | None | Write the push bodies to this file as JSON. |
| `--timeout` | seconds | `300` | The longest time one request may take, from 1 to 3600. |
| `--allow-http` | | Off | Send the token over `http` to another machine. |

A limit flag can only make the limit of the workspace smaller.

## Environment variables

| Variable | Meaning |
|---|---|
| `VELLUM_TOKEN` | The agent token, with the `push` scope. The CLI reads the token from this variable only. |
| `VELLUM_URL` | The address of the Vellum app, when `--url` is not given. |
| `VELLUM_PROJECT` | The project ID, when `--project` is not given. |

## What a scan reads

- Each regular file under the directory. The CLI does not follow a symbolic link, and it does not enter `node_modules`, `vendor`, `bin`, `obj`, `dist`, `.git` or a directory of secrets such as `.ssh`.
- The same path filter, scope, limits and content clearance as a server scan. See [Extraction rules](/reference/extraction-rules/).
- `.git/HEAD` as text, in the directory or the nearest directory above it, for the commit. No git program runs.

The CLI does not read `.gitignore`. The git blob SHA is computed from the bytes on disk. A checkout that converts line ends gives other SHAs than the git host.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Every directory was pushed, or scanned with `--dry-run`. |
| `1` | A directory could not be read. |
| `2` | Usage: a flag, a value, a directory or the token is missing or wrong. |
| `3` | The server refused the token: `401` or `403`. |
| `4` | The server refused the push: other extraction rules, a limit, a bad body, or another push of the Source is running. |
| `5` | The server failed or could not be reached. |

With several directories, the CLI tries each one and exits with the code of the first failure.

## HTTP calls

The CLI calls two endpoints, each with the token as a bearer credential:

- `GET /api/projects/{projectId}/sources/push` gives the rules of the server and the import limits of the workspace. The CLI reads no file when the rules differ from its own.
- `POST /api/projects/{projectId}/sources/push` takes the push and answers `201` with the Source, the scan and the Snapshot ID.

A push body is at most 24 MB.
