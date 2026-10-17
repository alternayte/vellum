---
title: Import a repository
description: Scan a GitHub repository at one commit. Vellum adds what it observes to the model, and a change to a fact you edited waits for you.
---

This guide shows you how to import a GitHub repository into a project. You need the editor role. A viewer can read the scans and the **Needs you** list.

A scan adds what it observes to the model, with you as the person who accepted it. It does not overwrite a fact that a person edited, and it deletes nothing. What it does not apply waits in **Needs you**.

A repository is a **Source** of the project, and each completed scan is a **Snapshot** of it. See [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/).

## Other kinds of Source

The rules for what a scan applies, **Needs you**, the limits and the mark "not seen in latest scan" on this page apply to each kind of Source. To read code from another place, see these guides:

- A repository on GitLab, Bitbucket Cloud or Azure DevOps: [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/).
- A folder on the machine that runs a local instance: [Import a local folder](/how-to/import-a-local-folder/).
- A `.zip` or `.tar.gz` file: [Upload an archive](/how-to/upload-an-archive/).
- Many repositories of one organization, group or workspace: [Add many repositories](/how-to/add-many-repositories/).

To delete a Source, see [Delete a Source](/concepts/sources-and-snapshots/#delete-a-source).

## Start a scan

1. Open the project and select **Sources** in the sidebar.
2. In **Repository**, give the repository as `owner/name` or as its GitHub URL. The URL must be on the GitHub host of the workspace.
3. In **Read**, select **Branch** and give a branch or a tag in **Branch or tag**, or select **Commit SHA** and give a full commit SHA. Leave **Branch or tag** empty to use the **Default ref** of the workspace settings. If that setting is empty, Vellum uses the default branch of the repository. To read a pull request, see [Check the impact of a change](/how-to/check-change-impact/#scan-a-pull-request).
4. To read only a part of the repository, give its folders in **Only these folders**. Separate the folders with commas. Leave the field empty to read all folders. One scan takes at most 50 paths.
5. Select **Scan repository**.

Vellum finds the commit that the ref points to and reads the files at that commit. It does not run the code. It does not read a file that looks like a secret store or a binary.

A private repository needs a token. See [Connect GitHub](/how-to/connect-github/).

The first scan of a repository adds it to **Sources**. To scan a Source again at its default ref, select **Scan again** on the Source.

A project runs one scan of a repository at a time. Vellum refuses a second scan of the same repository until the first scan ends.

## Watch the scan

The list shows each scan and its state.

| State | Meaning |
|---|---|
| Waiting to start | The scan is in the queue. Select **Cancel** to stop it. |
| Listing files, Reading files | The scan runs. Select **Cancel** to stop it. |
| Paused by the GitHub rate limit | The scan continues by itself at the time shown. |
| Scan complete | Vellum read every file in its limits. |
| Partial scan | A limit stopped the scan before it read every file. |
| Cancelled | You stopped the scan. Select **Resume** to continue from the files already read. |
| A failure message | The scan failed. The line says why and what to do. When a resume can help, select **Resume** to continue from the files already read. A failure that a resume meets again, for example a pull request that does not exist, has no **Resume**: start a new scan. |

The list updates by itself while a scan runs, also when the browser window is in the background. A scan that CI, the API or another session starts appears in the list within 15 seconds.

You can resume one scan 3 times. A scan that stopped more than 7 days ago cannot be resumed. Start a new scan.

## What a scan applies

When a scan of the tracked ref completes, Vellum applies to the model, in dependency order:

- Every addition: a thing that the source has and the model does not have.
- Every change of a fact that this Source wrote last and that no person edited since.

Vellum labels each applied fact **Observed in source**. A fact from a model file keeps the label of the file, **Declared**. Vellum records the Source, the commit or the Snapshot, the file and the lines, the person who started the scan and the time. An applied element is bound to its key in the Source. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

After the first scan, the canvas opens the top level of the view and places the new elements with the auto layout. A later scan places only the new elements. It does not move an element that already has a place.

A scan applies nothing from an AI provider. A proposal from a document or from the assistant always waits for a person.

A proposal states only what the source shows. A route in the code shows that the route is declared. It does not show that the route works.

The [extraction rules](/reference/extraction-rules/) list what Vellum reads and what each rule finds.

## Needs you

**Needs you** at the top of the **Sources** panel lists what the scans did not apply, one group for each Source. A card on the canvas shows the badge **needs you** when an item is about it or about a contract it owns.

| Item | Meaning | What you can do |
|---|---|---|
| Conflict | The source changed a fact that a person edited, or the value of another Source differs. The item shows both values. Neither wins until you decide. | **Accept** takes the value of the source. **Reject** keeps the model as it is. |
| Change | The source matches an entity that this Source did not write, such as a channel with the same name. | **Accept** links the entity to this Source and takes its values. **Reject** keeps the model. |
| Not seen | The latest Snapshot no longer holds a fact. See [Not seen in latest scan](#not-seen-in-latest-scan). | **Remove** or **Keep**. |
| Same as? | An element of this scan has the name of an element in the model. | **Same** binds the element to this Source. **Not the same** proposes a new element. |
| Match | The extraction rules changed and Vellum cannot tell which found fact is the one in the model. | Select **Choose**, select the fact or **None of these**, then select **Apply**. |
| Add | An addition that the model refused, for example because an element of that name came into the model after the scan. | **Accept** or **Reject**. The item says why the model refused it. |

Select an item to see its fields and the source lines that support it. The **GitHub** link opens the same lines on GitHub at the scanned commit. The detail panel of a selected element also shows its items, with **Accept** and **Reject**.

Accepting an item also accepts the items it depends on. For example, an operation needs its service.

If an accepted item breaks something, such as a scenario that uses a removed operation, the item names what breaks. Select **I accept what this breaks** to accept it anyway.

Vellum remembers a rejection. It proposes the same thing again when the source or the fact changes. It can also propose it again when a change to the extraction rules gives the fact a new key.

A person edits an imported fact, and the source does not change it: Vellum proposes nothing. The edit stays. Only a later change of the same fact in the source puts an item in **Needs you**.

`GET /api/projects/{projectId}/needs-you` returns the list. Decide an item with `POST /api/projects/{projectId}/imports/{scanId}/proposals/decide`, on the `scanId` of its Source.

## Scan again

Start a new scan of the same repository to compare the model with newer code.

- The first scan that applies a fact sets the **tracked ref**: the branch or tag that the model follows.
- A scan of the tracked ref applies its additions and the changes of facts that nobody edited. It replaces the items of earlier scans of the same ref in **Needs you**.
- A scan of another branch or tag is a **comparison**. It applies nothing. Select **Impact** to read what it would change. Select **Follow this ref** to make that ref the tracked ref: Vellum then applies its scan as you. Only the newest completed scan of a ref has this effect.
- A scan of a bare commit or of a pull request is always a comparison. It cannot become the tracked ref.

To see what a change in the code does to the model before it reaches the tracked ref, see [Check change impact](/how-to/check-change-impact/).

## Not seen in latest scan

A scan deletes nothing. When a scan looks at the source of an applied fact and no longer finds it, Vellum marks the fact **not seen in latest scan**. Only a scan of the same Source can do this. A fact that another Source still holds gets no mark. The card shows the badge **not seen**, and **Needs you** lists the fact.

Select the element. The detail panel shows **Not seen in latest scan** with two actions:

- **Remove** takes the fact out of the model.
- **Keep** leaves the fact and clears the mark. Later scans do not mark it again while its values stay the same. A later change of the fact, by a Snapshot or by a person, makes the mark possible again.

Vellum sets the mark only on a fact whose label is the label of its Source. A fact that a person took over as **Declared** gets no mark.

A scan judges a fact only when it read the files that the fact came from. A scan with fewer paths, a partial scan, or a scan that left a file out says nothing about the facts in those files.

A scan of some folders can find a thing that the model already has, and not see all of it. An example is a consumer whose message contract is in another folder. Vellum does not add that thing again. Scan all folders to judge it.

A later scan that finds the evidence again clears the mark.

`POST /api/projects/{projectId}/needs-you/keep` with `entityKind` and `entityId` keeps a fact.

## Limits

A workspace owner sets the limits of a scan under **Settings → Import limits**.

| Setting | Default |
|---|---|
| **Files per import** | 2,000 |
| **Largest file** | 262,144 bytes |
| **Total size** | 20,971,520 bytes |
| **Excluded paths** | `**/node_modules/**`, `**/vendor/**`, `**/bin/**`, `**/obj/**`, `**/*.min.*` |

A scan uses the limits that were set when it started.

**Settings → Usage limits** sets **Repository scans per day**. The default is 20 for one workspace. The day is a UTC day. A resume that must ask GitHub for files again counts as one scan. When the limit is reached, Vellum refuses the scan with status `429` and the code `scan_limit_reached`.

A scan applies only things that have a place in the model. Other facts, such as an operation that has no owning service in the source, are kept as evidence. `GET /api/projects/{projectId}/imports/{importId}/evidence` returns them, each with its kind, its name, the reason, the attributes the source states and the cited lines.

## See also

- [Sources, Snapshots and bindings](/concepts/sources-and-snapshots/)
- [Import a repository and read the result](/tutorials/import-and-review/)
- [Connect GitHub](/how-to/connect-github/)
- [Connect GitLab, Bitbucket or Azure DevOps](/how-to/connect-a-git-host/)
- [Import a local folder](/how-to/import-a-local-folder/)
- [Upload an archive](/how-to/upload-an-archive/)
- [Add many repositories](/how-to/add-many-repositories/)
- [Check change impact](/how-to/check-change-impact/)
- [Extraction rules](/reference/extraction-rules/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
- [Design with the assistant](/how-to/design-with-the-assistant/#private-repositories)
