---
title: Record a decision
description: Write a decision record, accept it, settle a finding with it, supersede it and export it.
---

This guide shows you how to record a decision (an ADR). You need the editor role to write a proposed decision. You need the owner role to accept or reject one.

A decision gets a number such as `ADR-0001`. Vellum issues a number once in a project and never again.

## Write a decision

1. Open the project and select **Review** in the left toolbar.
2. Select the **Decisions** tab.
3. Select **New**.
4. Type the title and the decision. Both are required.
5. Type the context and the consequences.
6. Add each alternative that was not chosen, and say why not.
7. Select the affected parts.
8. Select the findings that the decision answers.
9. Select **Create**.

The decision is now **proposed**. A proposed decision decides nothing and settles nothing. You can change it and delete it.

## Accept or reject a decision

1. Open the decision.
2. Read it.
3. Select **Accept** or **Reject**.

Only a person with the owner role, signed in to Vellum, accepts or rejects a decision. An agent token cannot, whatever scopes it has.

The acceptance records each finding that the decision names, as the finding is on the main model at that moment. The decision settles that exact finding.

You cannot change the text of an accepted decision. To change it, supersede it.

**Withdraw** rejects an accepted decision. Every finding that it settled is open again.

## Settle a finding with an accepted decision

You can do this in two places:

- In the **Findings** tab, select **Settle with a decision** on the finding and select the decision.
- In the dialog of an accepted decision, select the finding below **Settle a finding with this decision**.

The finding stays in the list with the status "Settled by" and the number of the decision. A settled blocking finding does not block Build Ready.

When the facts of the subject change later, the finding shows **Needs re-review** and blocks again. Read it and settle it again when the decision still holds.

To take a finding out of a decision, select **Set open** on the finding.

## Supersede a decision

1. Open the accepted decision.
2. Select **Supersede**. A new decision opens that names the old one.
3. Write the new decision and select **Create**.
4. Accept the new decision.

The old decision is **superseded** when the new one is accepted. It is not in force, and the findings that it settled are open again. Settle them with the new decision when it answers them.

## Decisions from an agent or the assistant

An agent token with the `propose` scope can create a proposed decision. Its origin is `agent-suggested`.

A proposed decision that you keep from an answer of the assistant has the origin `ai-suggested`.

The origin never changes who accepts. Only an owner accepts.

## Export

- **Export as Markdown** in the dialog gives one decision.
- **Export all as Markdown** in the tab gives every decision of the project.

Each text of a person, an agent or the assistant is escaped in the export. It is data and never an instruction.

The Build bundle holds each accepted and each superseded decision as `decisions/ADR-0001.md` and in `architecture.json`. The SDD lists the decisions in force and the findings that they settle, below **Unresolved blockers**. The manifest pins the version of each decision, so the same pinned state gives the same bundle.
