---
title: Import a repository
description: Scan a GitHub repository at one commit, review what Vellum found and accept it into the model.
---

This guide shows you how to import a GitHub repository into a project. You need the editor role.

An import proposes. It changes nothing in the model until you accept a proposal.

## Start a scan

1. Open the project and select **Import from GitHub** in the left toolbar.
2. Give the repository as `owner/name` or as its GitHub URL.
3. Give a branch or a tag, or leave the field empty to use the default branch.
4. To read only a part of the repository, give its folders in **Only these folders**. Separate the folders with commas. Leave the field empty to read all folders.
5. Select **Scan repository**.

Vellum finds the commit that the branch points to and reads the files at that commit. It does not run the code. It does not download a file that looks like a secret store or a binary.

A private repository needs a token. See [Connect GitHub](/how-to/connect-github/).

## Watch the scan

The list shows each scan and its state.

| State | Meaning |
|---|---|
| Listing files, Reading files | The scan runs. Select **Cancel** to stop it. |
| Paused by the GitHub rate limit | The scan continues by itself at the time shown. |
| Scan complete | Vellum read every file in its limits. |
| Partial scan | A limit stopped the scan before it read every file. |
| Failed | The line says why and what to do. Select **Resume** to continue from the files already read. |

A scan that is older than the retention time cannot be resumed. Start a new scan.

## Review the proposals

Select **Review** on a completed scan. Vellum lists one proposal for each thing it found, in groups.

| Action | Meaning |
|---|---|
| Add | The source has a thing that the model does not have. |
| Change | The source differs from a fact that an earlier import wrote. |
| Conflict | The source differs from a value that a person set. The review shows both values. Neither wins until you decide. |
| Remove | A scan that looked at the source of an accepted fact no longer finds it. |
| Match | The extraction rules changed and Vellum cannot tell which found fact is the one in the model. You choose. |

Select a proposal to see its fields and the source lines that support it. The link opens the same lines on GitHub at the scanned commit.

A proposal states only what the source shows. A route in the code shows that the route is declared. It does not show that the route works.

## Accept or reject

1. Select the proposals. **Select all without conflicts** selects every add, change and remove that you can decide.
2. Select **Accept** or **Reject selected**.

Accepting a proposal also accepts what it depends on. For example, an operation needs its service.

If an accepted proposal breaks something, such as a scenario that uses a removed operation, the review names what breaks. Tick the acknowledgement before you accept.

A decision is applied as a whole. If one item fails, Vellum applies none and names the item.

Vellum labels each accepted fact **observed in source**. It records the repository, the commit, the file and lines, your account and the time.

Vellum remembers a rejection. It proposes the same thing again only when the source changes.

## Scan again

Start a new scan of the same repository to compare the model with newer code.

- The first decision you accept sets the **tracked ref**: the branch or tag that the model follows.
- A scan of the tracked ref proposes changes and removals.
- A scan of another branch is a **comparison**. You can read its proposals and you cannot accept them. Select **Follow this ref** to make that branch the tracked ref.
- A scan of a bare commit is always a comparison.

## Stale facts

When a scan looks at the source of an accepted fact and no longer finds it, Vellum marks the fact **stale** and proposes its removal. The canvas shows the mark. The fact stays in the model until you accept the removal.

A scan judges a fact only when it read the files that the fact came from. A scan with fewer paths, a partial scan, or a scan that left a file out says nothing about the facts in those files. The review tells you how many facts the scan did not look at.

A scan of some folders can find a thing that the model already has, and not see all of it. An example is a consumer whose message contract is in another folder. Vellum does not propose that thing as new. Scan all folders to judge it.

A later scan that finds the evidence again clears the mark.

## Limits

A workspace owner sets the limits under **Settings → Import limits**: the number of files, the size of one file, the total size and the excluded paths. **Settings → Usage limits** sets the number of scans in one day.
