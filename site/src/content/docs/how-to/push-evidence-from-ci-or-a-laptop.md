---
title: Push evidence from CI or a laptop
description: Run vellum scan where the code is, and push the manifest, the facts and the citations to a project. No file content leaves the machine.
---

This guide shows you how to import evidence from code that Vellum cannot read. You run `vellum scan` on a machine that has the code: a laptop or a CI runner. It runs the same extractors as the server and pushes the result to a project. No file content leaves the machine.

Use this when your code cannot go to Vellum: a repository on a host that Vellum does not read, a repository that policy keeps away from other services, or a directory that is not in git.

You need the editor role in the project. The workspace owner creates the token.

## Know what is sent

`vellum scan` sends the manifest (the path, the size and the git blob SHA of each file that it read), the facts with their citations, the versions of the extraction rules, and the commit that `.git/HEAD` names. It does not send the content of a file. See [Security model](/concepts/security-model/#what-vellum-scan-sends).

To read the exact body before you push anything, scan with `--dry-run` and `--payload`:

<!-- snippet: syntax -->
```sh
vellum scan ./services/orders --dry-run --payload push.json
```

## Get the CLI

The CLI is one file with no other dependency. Each [release](https://github.com/alternayte/vellum/releases) has a file for `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` and `win-x64`, and a `SHA256SUMS` file. For example, version 0.2.0 on a Linux runner:

<!-- snippet: syntax -->
```sh
curl -fsSL https://github.com/alternayte/vellum/releases/download/v0.2.0/vellum-0.2.0-linux-x64.tar.gz | tar -xz
./vellum --version
```

To build it from the source instead, run `just cli` (this machine) or `just cli linux-x64` in the repository. The file is written to `artifacts/cli/<rid>/vellum`.

Use the CLI of the same version as your Vellum server. A CLI with other extraction rules is refused before it reads a file, with exit code `4`.

## Create a token with the push scope

1. Open the workspace **Settings**, then **Agent tokens**.
2. Enter a name, for example `orders CI push`.
3. Select **Push evidence** only.
4. Select **Create token** and copy the token.

The token acts for the person who created it. That person needs the editor role or higher in the project.

## Push from a laptop

1. Copy the project ID from the address of the project in the app.
2. Set the token in the environment. The CLI reads it from `VELLUM_TOKEN` only, so the token is not in your shell history or in the process list.
3. Run the scan.

<!-- snippet: syntax -->
```sh
export VELLUM_TOKEN='vlm_agt_…'
vellum scan ./services/orders ./services/billing \
  --url https://vellum.example.com \
  --project 0f8fad5b-d9cb-469f-a165-70867728950e
```

Each directory is one Source. The folder name is the Source name. To give another name, add `--name ./services/orders=Orders API`. A second push with the same name goes to the same Source and makes a new Snapshot.

The CLI prints one line for each directory: the counts, the Snapshot ID and how many items wait in **Needs you**.

## Push from CI

This GitHub Actions job pushes the repository on each push to `main`. Add `VELLUM_TOKEN` as a secret and `VELLUM_URL` and `VELLUM_PROJECT` as variables. Build the CLI for `linux-x64` and keep it where the job can download it, for example as a release asset of your own.

<!-- snippet: syntax -->
```yaml
name: Push evidence to Vellum
on:
  push:
    branches: [main]
jobs:
  push:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Get the vellum CLI
        run: curl -fsSL "https://github.com/alternayte/vellum/releases/download/v$VELLUM_VERSION/vellum-$VELLUM_VERSION-linux-x64.tar.gz" | tar -xz
        env:
          VELLUM_VERSION: ${{ vars.VELLUM_VERSION }}
      - name: Push the facts
        run: ./vellum scan . --name orders
        env:
          VELLUM_TOKEN: ${{ secrets.VELLUM_TOKEN }}
          VELLUM_URL: ${{ vars.VELLUM_URL }}
          VELLUM_PROJECT: ${{ vars.VELLUM_PROJECT }}
```

The job fails when the exit code is not `0`. See [vellum CLI](/reference/vellum-cli/#exit-codes).

## What a push adds

A push is a scan of the machine that pushed. It applies what it observed as the person who made the agent token: every addition, and each change of a fact that only this Source wrote and that no person edited. The rest waits in **Needs you**. See [Needs you](/how-to/import-a-repository/#needs-you).

1. Open the project and the **Sources** panel.
2. Under **Sources**, find the Source. Its kind is `agent-push`, and its Snapshot says **content not held**.

The facts of a push are `observed-in-source`. Their citations name the Snapshot: `agent-push/orders@<snapshot>:path:lines`.

Vellum does not hold the files, so these features are off for a pushed Snapshot, and the app says why:

- The source view next to an item of **Needs you**. Open the file on the machine that pushed.
- **Scan again** on the Source. Run `vellum scan` again.

A later push of the same Source compares with the last one. A fact that the new push no longer finds where it looked gets the mark **not seen in latest scan**. A push deletes nothing, and it never changes a fact that a person edited.

## Limit what is scanned

The workspace settings `import.maxFiles`, `import.maxFileBytes`, `import.maxTotalBytes` and `import.excludeGlobs` apply to a push. The CLI reads them from the server before it scans. The flags `--max-files`, `--max-file-bytes`, `--max-total-bytes`, `--include`, `--exclude` and `--exclude-glob` can make the scope smaller, never larger.

The CLI does not read `.gitignore`. It skips dependency and build directories, `.git`, secret files by name, binary files, and files whose content is a secret, as a server scan does.

## If the push fails

| Exit code | Cause | What to do |
|---|---|---|
| `2` | A flag or a value is missing or wrong. | Read the message. `vellum --help` lists the flags. |
| `3` | The token is revoked or expired, has no `push` scope, or its user is not an editor of the project. | Create a token with the push scope. |
| `4` | The server refused the push: other extraction rules, a limit, or another push of the Source is running. | Use the CLI of the server's version, make the scope smaller, or try again. |
| `5` | The server failed or could not be reached. | Check `--url` and the network, then try again. |
