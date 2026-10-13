---
title: Design with the assistant
description: Ask for a flow or for additions to the model, review the proposal, and get an explanation.
---

This guide shows you how to use the assistant. You need the editor role to ask for a proposal and to decide it. A viewer can ask for an explanation.

The assistant needs an AI provider. See [Connect an AI provider](/how-to/connect-an-ai-provider/). Without one, the assistant says "No AI provider is set for this task", and the rest of Vellum works as before.

The assistant proposes. It never changes the model or a scenario by itself.

## Ask for a flow

1. Open the project and select **Assistant** in the left toolbar.
2. Select **Flow**.
3. In **Describe the behaviour**, describe the behaviour in plain words. For example: "A customer places an order. Orders saves it and publishes OrderCreated. Show what happens when Billing fails." The text holds at most 4,000 characters.
4. Select **Propose a flow**.

The proposal opens. Nothing is saved yet.

## Read a flow proposal

| Part | Meaning |
|---|---|
| Steps that can be saved | The steps whose parts all exist in the model. The line below them says how many events the flow plays as. |
| Parts the flow needs and the model lacks | Things the flow uses that the model does not have. Select **Create** to add one. Vellum labels it inferred and records the proposal as its source. |
| Left out | Places where the assistant referred to something that does not exist. Vellum removed those parts. |
| Problems the trace check found | References or payloads that the trace engine refuses. |
| Questions the assistant could not answer | What the request left open. Select **Keep** to make a question an open question of the project. |

The assistant sees the parts of your model as a numbered table, without their real IDs. It can use only what is in that table. When it names something outside the table, Vellum leaves that out.

When the assistant asks for a part that the model already has, Vellum uses the existing part and says so.

Some missing parts need another part first. For example, an operation needs its service. The entry then says which part to create first. A step that waits for a missing part is not in the list of steps until you create the part.

## Decide

- **Accept and play** saves the flow as a scenario and plays it on the canvas. The button has this label when the trace check found no problem.
- **Accept with these problems** saves a flow that has problems. Correct them in the scenario editor. The button has this label when the trace check found a problem.
- **Reject** records the rejection. Nothing is saved.
- **Close** closes the dialog and leaves the proposal undecided.

You cannot accept a proposal that has no step that can be saved. Create the missing parts first, or reject the proposal.

If the model changes after the assistant made the proposal, the dialog says so and shows the steps and the problems for the model as it is now. If Vellum refuses the accept for this reason, select **Load the current check**. Then decide again.

A scenario that came from a proposal records that origin. To edit or play it later, see [Trace a scenario](/how-to/trace-a-scenario/).

## Ask for additions to the model

1. Select **Model** in the assistant panel.
2. In **Describe what to add**, describe what to add. For example: "Add a notification service that listens to OrderCreated." The text holds at most 4,000 characters.
3. Select **Propose additions**.

The proposal lists each addition with its reason and with the confidence the assistant states, shown as `inferred` and a percentage. An addition is an element, a relationship, an operation, a channel, a subscription, a binding or a message. One proposal holds at most 200 additions.

Select the ones you want. Then select the accept button, for example **Accept 3**, or select **Reject selected**. Accepting an item also accepts what it needs.

Each accepted item is added with the label **Inferred**. A proposal never changes or removes what is in the model. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

The confidence is what the assistant claims. It is not a measurement.

When the assistant proposes a part that the model already has, Vellum does not propose it again and names the part below the list.

## Ask for an explanation

1. Select an element on the canvas, or open a trace and go to an event.
2. Select **Explain** in the assistant panel.
3. Add a question in **Your question (optional)** if you want, and select **Explain**. The question holds at most 2,000 characters.

When a trace is open at an event, the assistant explains that event and not the selected element.

The answer appears as it is written. Select **Stop** to end it.

An explanation reads the declared model. It verifies nothing. See [A trace is declared behaviour](/concepts/traces-are-declared/).

## Private repositories

Parts of the model that came from a private repository go to an AI provider only when a workspace owner turns on **Send private repository code to AI providers** under **Settings → Privacy**. The setting is off by default. The instance policy must also allow it.

With that setting off, the assistant does not see those parts, and the proposal or the explanation says how many it did not see.

Vellum treats a part as private when an import wrote it and the scan did not record the repository as public. A part with the label declared is never treated as private.

See [What leaves your instance](/concepts/what-leaves-your-instance/).

## Limits

- A project holds at most 50 undecided proposals of the two kinds together. At the limit, Vellum refuses a new proposal with status `409` and the code `too_many_drafts`.
- The web client has no list of proposals. A proposal that you close and do not decide stays undecided, and you cannot open it again in the web client. Use the API to list and delete such proposals: `GET /api/projects/{projectId}/ai/flow-proposals`, `DELETE /api/projects/{projectId}/ai/flow-proposals/{proposalId}`, `GET /api/projects/{projectId}/ai/architecture-proposals` and `DELETE /api/projects/{projectId}/ai/architecture-proposals/{proposalId}`. See [Use the HTTP API](/how-to/use-the-http-api/).
- Vellum deletes only an undecided proposal. It does not delete a proposal for additions that has a decided item. Reject the other items to close it.
- One person can make 30 requests to the assistant in one minute. One project takes 90 requests in one minute. Over a limit, Vellum answers with status `429`.
- One person can have 2 explanations in progress at one time.
- A large model does not fit the numbered table in full. The table holds at most 192 KiB. The web client does not show how many parts the table left out. The proposal that the API returns gives the numbers in `omitted`.
- An error about a missing part that cannot be created names the number of the part in the proposal, such as `N1`, and not its name.
