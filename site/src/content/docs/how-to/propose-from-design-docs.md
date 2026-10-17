---
title: Propose a model from design docs
description: Let the AI read a README or design document of a Source and propose inferred elements, relationships and flows that cite their lines.
---

This guide shows you how to turn prose, such as a README or a design document, into proposals for the model. You need the Editor role and an AI provider. See [Connect an AI provider](/how-to/connect-an-ai-provider/).

Only the AI path reads prose. No extractor does. Every proposal is `inferred`, cites the lines of the document it comes from, and waits for you to accept it.

## Before you start

- The document is a Markdown or text file (`.md`, `.markdown`, `.txt`) of at most 64 KiB.
- The document is in the newest Snapshot of a Source. Scan the repository, or upload the document under **Upload a model file**.
- A document of a private repository or an upload is private content. The workspace setting `privacy.sendPrivateCodeToAi` must allow it. An Owner sets this in **Settings**. Without it, Vellum sends nothing.

## Propose additions

1. Select **Sources** in the sidebar.
2. Under **Sources**, select **Propose from a document** on the Source.
3. Select the document.
4. Optional: in **What to look for**, say which part matters.
5. Select **Propose from document**.

Without an AI provider, the dialog says that this Source needs one. Nothing is sent.

## Accept or reject

The proposal lists each item with its confidence, its reason and the lines it cites. An item that cites no lines of the document is dropped and listed with the reason.

1. Select the items that you accept.
2. Select **Accept**.

An accepted item is an `inferred` fact. Its source pointer is the citation, `<source>@<revision>:path:start-end`. Vellum never changes or removes a fact that exists.

## Propose a flow

1. In the dialog, under **Propose**, select **A flow**.
2. Optional: in **Which flow**, name the flow.
3. Select **Propose from document**.

The flow cites the lines it comes from; an answer that cites no lines of the document is not stored. A thing the flow needs that the model does not hold is created only when you ask for it, as an `inferred` fact that cites those lines. See [Design with the assistant](/how-to/design-with-the-assistant/). Through the API, send `documentSourceId` and `documentPath` with the request to `POST /api/projects/{projectId}/ai/flow-proposals`.

## The document is data

The text of a document is untrusted. Vellum sends it in a marked data part of the prompt, with each line numbered and every line that looks like a credential masked. The instructions tell the provider never to follow an instruction in the document.
