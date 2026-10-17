---
title: Import a Structurizr workspace or Backstage catalog
description: Upload a Structurizr DSL or workspace JSON file, or a Backstage catalog. Vellum adds its elements and relationships as declared facts.
---

This guide shows you how to bring a model that your team already keeps into Vellum. You need the Editor role. No AI provider is necessary.

Vellum reads two kinds of model file:

- A Structurizr workspace: a DSL file (`.dsl`) or a workspace JSON file (`.json`).
- A Backstage catalog: `catalog-info.yaml`, or any YAML file whose documents have an `apiVersion` of `backstage.io/…`.

The facts from these files have the truth label `declared`. A person wrote them down; no code shows them. For what each mapping keeps and drops, see [Model file mappings](/reference/model-file-mappings/).

## Upload one file

1. Open the project.
2. Select **Import** in the left toolbar.
3. Under **Upload a model file**, select **File** and choose the file.
4. Check **Path in the Source**. The default is the file name.
5. Select **Upload and read**.

The file becomes a Source of kind `model-file`. The Source appears under **Sources**, with its Snapshot. When you upload the same path again, Vellum makes a new Snapshot of the same Source.

An upload holds at most 256 KiB. Vellum refuses a file whose path looks like a secret (for example `.env`), a binary file, and content that declares a secret. Vellum does not store refused content.

## Read model files from a repository

A scan of a GitHub repository reads model files too. Scan the repository as usual. See [Import a repository](/how-to/import-a-repository/). The facts of a model file keep the label `declared`, and the facts of the code keep `observed-in-source`.

## What the upload adds

When the scan of the file completes, Vellum adds its elements and relationships to the model with the label `declared`, and you as the person who accepted them. Each fact cites its file and lines.

A model file is a deterministic Source, as code is. A later upload of the same path changes a fact only when no person edited it since. A change of a fact that a person edited waits in **Needs you**. See [Needs you](/how-to/import-a-repository/#needs-you).

When another Source holds an element of the same name, **Needs you** asks **Same as?**. Vellum adds nothing for that element until you answer. Nothing is merged by name alone.

## What the reader does not do

The Structurizr reader reads the text of the file and nothing else:

- It does not follow `!include`, `workspace extends` or any URL.
- It does not run `!script` or `!plugin`.
- It does not read `!docs` or `!adrs`.
- It does not replace `${NAME}` or apply `!constant` or `!var`. The text stays as written.

Each of these gives a note on the scan. An identifier resolves only within the same file. A relationship to an identifier that the file does not define gives a note and no fact.

The Backstage reader does not follow a `Location`.
