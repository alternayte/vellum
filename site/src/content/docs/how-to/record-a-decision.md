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
6. Below **Alternatives that were not chosen**, select **Add** for each alternative, and say why not.
7. Below **Affected parts**, select the parts of the model that the decision is about.
8. Below **Findings it answers**, select the findings that the decision answers. The list shows the concerns and the unknowns of the model that you have open.
9. Select **Create**.

The decision now has the status `proposed`. A proposed decision decides nothing and settles nothing. You can change it with **Save** and remove it with **Delete**.

A project holds at most 500 decisions. One decision holds at most 20 alternatives, 200 affected parts and 100 findings.

## Accept or reject a decision

1. Open the decision.
2. Read it.
3. Read the list below **What an acceptance records**. It shows each finding that the decision names, as the finding is now, and it says which model the findings are of.
4. Select **Accept** or **Reject**. A reason for a rejection is optional.

![The dialog of ADR-0001 · Orders API owns the Orders DB. It says: This is a proposal. It decides nothing until an owner accepts it. Origin: human. The fields Title, Context, Decision and Consequences are filled in. One alternative is listed: A database for the worker, with the reason The outbox needs one transaction. Below Affected parts, Orders API, Orders DB and Order worker are selected. Below Findings it answers, CON-005 · Orders DB is selected. The buttons are Reject, Delete, Save and Accept.](../../../assets/screenshots/decision-proposed.png)

**Accept** is not available while the dialog has changes that are not saved. Select **Save** first.

Only a person with the owner role, signed in to Vellum, accepts or rejects a decision. An agent token cannot, whatever scopes it has.

The acceptance records each finding in that list. The decision settles that exact finding. The findings are of the model that you have open: the main model, or a draft or a variant. A finding can be another finding on another model. There it shows `needs re-review` until you settle it again.

Vellum accepts or rejects only the decision that you read. When another person changed the decision, accepted it or rejected it after you opened it, Vellum refuses and changes nothing. Select **Reload the decision**, read it again, then decide.

Over HTTP, each read of a decision gives its `contentHash`. Send that value as `contentHash` to `POST /api/projects/{projectId}/decisions/{decisionId}/accept` and `POST /api/projects/{projectId}/decisions/{decisionId}/reject`. A request with no `contentHash` gives the status 400. Send `branchId` to `accept` when you read the findings of a draft or a variant, or `revisionId` when you read the findings of a named revision. `reject` also takes an optional `reason`. A decision that is not the one you read gives the status 409 and the type `decision_changed`, with the `contentHash` of the decision as it is now. See [Conflicts](/troubleshooting/conflicts/).

This body accepts a decision after a read of the findings of the main model:

```json
{
  "contentHash": "<contentHash>"
}
```

You cannot change the text of an accepted decision. To change it, supersede it.

**Withdraw** rejects an accepted decision. Every finding that it settled is open again.

You cannot delete a decision that was accepted. It is a record. Withdraw it or supersede it.

## Settle a finding with an accepted decision

You can do this in two places:

- In the **Findings** tab, select **Settle with a decision** on the finding, select the decision, then select **Settle**.
- In the dialog of an accepted decision, go to the heading **Settle a finding of the main model with this decision**. In a draft or a variant, the heading is **Settle a finding of the draft or variant that is open with this decision**. Select the finding, then select **Settle**.

You need the owner role. Only a person signed in to Vellum settles a finding. Only a concern or an unknown can be settled.

The finding stays in the list with the text `Settled by` and the number of the decision. A settled blocking finding does not block Build Ready.

One decision in force settles a finding. When another decision in force names the finding already, Vellum refuses with the status 409. Set the finding to open first.

When the rule changes or the facts of the subject change later, the finding shows **Needs re-review** and blocks again. Read it. When the decision still holds, select the button **Settle again with** and the number of the decision.

To take a finding out of a decision, select **Set open** on the finding. You need the owner role.

## Supersede a decision

1. Open the accepted decision.
2. Select **Supersede**. A new decision opens that names the old one.
3. Write the new decision and select **Create**.
4. Accept the new decision.

The old decision has the status `superseded` when the new one is accepted. It is not in force, and the findings that it settled are open again. Settle them with the new decision when it answers them.

## Decisions from an agent or the assistant

An agent token with the `propose` scope can create a proposed decision. Its origin is `agent-suggested`.

A proposed decision that you keep from an answer of the assistant, with **Keep as proposed decision**, has the origin `ai-suggested`. See [Review a design](/how-to/review-a-design/#ask-the-assistant).

An agent token cannot change, accept, reject or delete a decision.

The origin never changes who accepts. Only an owner accepts.

## Export

- **Export as Markdown** in the dialog gives one decision.
- **Export all as Markdown** in the tab gives every decision of the project.

Over HTTP, `GET /api/projects/{projectId}/decisions/{decisionId}/export` gives one decision and `GET /api/projects/{projectId}/decisions/export` gives every decision. Both give Markdown. An agent token with the `read` scope can call both.

Each text of a person, an agent or the assistant is escaped in the export. It is data and never an instruction.

The Build bundle holds each accepted and each superseded decision as a file such as `decisions/ADR-0001.md` and in the `decisions` member of `architecture.json`. A proposed or rejected decision is not in the bundle. The SDD lists the decisions in force and the findings that they settle, in the subsection `Decisions` of the section `12. Unresolved blockers`. The pinned state of the bundle names the version of each decision, so the same pinned state gives the same bundle.

## Next

- [Review a design](/how-to/review-a-design/) to read the findings that a decision answers.
- [Review rules](/reference/review-rules/) for how to settle a concern of each rule.
- [Findings are not proof](/concepts/findings-are-not-proof/) for what a settled finding means.
- [Export for an agent](/how-to/export-for-an-agent/) for the bundle and the SDD.
- [Connect a coding agent](/how-to/connect-a-coding-agent/) to let an agent propose a decision.
- [Conflicts](/troubleshooting/conflicts/) when Vellum refuses an acceptance.
