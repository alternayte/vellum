---
title: Review a design
description: Read the findings of the review rules, answer them, save a snapshot and ask the assistant.
---

This guide shows you how to review a model. A viewer can read the findings. You need the editor role to answer a finding with "won't fix", to save a snapshot and to ask the assistant. You need the owner role to settle a finding with a decision.

The review rules need no AI provider. They read the model, the scenarios, the schemas and the promises. The same model always gives the same findings and the same hash.

A finding says what the declared model shows. It is not evidence about a running system. See [Findings are not proof](/concepts/findings-are-not-proof/).

## Read the findings

1. Open the project and select **Review** in the left toolbar.
2. Read the summary. It says how many findings block Build Ready.
3. Read the list. The list shows concerns and unknowns first.

Each finding has one of four results:

| Result | Meaning |
|---|---|
| Verified | The declared model satisfies the rule. |
| Concern | The declared model does not satisfy the rule. |
| Unknown | The rule needs a fact that the model does not hold, or the fact is inferred, unknown or stale. |
| Not applicable | The rule does not read this part. |

Each finding names its rule, its subject and the parts it cites. Select a part to show it on the canvas. A contract is shown through the element that owns it.

Select **What would change this** to see which change of the model gives another result.

Use the filters to show verified and not-applicable results, or to show one severity or one category.

The page [Review rules](/reference/review-rules/) describes each rule.

## Understand what blocks Build Ready

A finding blocks Build Ready only when its severity is **blocking** and its result is **concern**. A rule gives the blocking severity only for a part on the path of an approved promise.

A blocking finding never stops you from saving. It adds a blocker `review-<rule ID>` to the Build manifest and to the SDD. The export still works.

## Answer a finding

You have three answers:

- **Change the model.** The finding changes with the next read.
- **Settle with a decision.** Select an accepted decision. Only this answer removes a blocker. See [Record a decision](/how-to/record-a-decision/).
- **Won't fix.** Give a reason. This answer is for warnings and notes. It never settles a blocking finding.

Only a person signed in to Vellum answers a finding. An agent token cannot.

A finding that you answered stays in the list. It shows its answer.

## Review an answer again

An answer records what the finding was. When the rule changes or the facts of the subject change, the finding shows **Needs re-review**. The answer then settles nothing.

1. Read the finding again.
2. Select **Settle again** or **Review again** when the answer still holds.
3. Select **Set open** when it does not.

## Save a snapshot

A snapshot keeps the findings of one model version, with their answers.

1. Type a note. The note is optional.
2. Select **Save snapshot**.

The list shows the model version and the hash of each snapshot. It says **same findings** when the hash is the hash of the findings now.

## Keep open questions

Select the **Questions** tab. Add what the design leaves undecided. Answer a question, or drop it.

A question that the assistant asks in a flow proposal, an architecture proposal or a critique has a **Keep** button. The button makes it an open question of the project.

## Ask the assistant

The assistant needs an AI provider. See [Connect an AI provider](/how-to/connect-an-ai-provider/). Without one, the tab says so, and the findings work as before.

1. Select the **Assistant** tab.
2. Type a question. The question is optional.
3. Select **Ask the assistant**.

The answer is in its own frame with the label **Suggestions of the assistant**. Nothing in it is a finding.

| Part | Meaning |
|---|---|
| Risks that a finding supports | A risk that rests on a finding of a rule for a part it names. The finding is the fact. The sentence is the assistant's. |
| Questions | What the assistant asks you. |
| Suspicions the model does not show | A claim with no finding behind it. Vellum shows it as a question and says why the model does not show it. |
| Proposed decisions | A decision that you can keep as a proposal. Only an owner accepts it. |
| Left out | What Vellum removed from the answer, with the reason. |

The assistant sees the parts of your model as a numbered table, without their real IDs. Vellum removes each item that names something outside the table. Vellum also removes each item that states that something is verified, proven, safe, secure, scalable, compliant, guaranteed or resolved.

When the model changes after the answer, the answer is stale. Ask again before you keep a proposed decision.
