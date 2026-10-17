---
title: Design with the assistant
description: Ask for a flow or for additions to the model, review the proposal, and get an explanation.
---

This guide shows you how to use the assistant. You need the editor role to ask for a proposal. A viewer can ask for an explanation.

The assistant needs an AI provider. See [Connect an AI provider](/how-to/connect-an-ai-provider/). Without one, the assistant says so, and the rest of Vellum works as before.

The assistant proposes. It never changes the model or a scenario by itself.

## Ask for a flow

1. Open the project and select **Ask the assistant** at the bottom of the canvas. You can also press `Ctrl+K` or `Cmd+K` and select **Ask the assistant**.
2. Select **Flow**.
3. Describe the behaviour in plain words. For example: "A customer places an order. Orders saves it and publishes OrderCreated. Show what happens when Billing fails."
4. Select **Propose a flow**.

The proposal opens. Nothing is saved yet.

## Read a flow proposal

| Part | Meaning |
|---|---|
| Steps that can be saved | The steps whose parts all exist in the model. The line below them says how many events the flow plays as. |
| Parts the flow needs and the model lacks | Things the flow uses that the model does not have. Select **Create** to add one. Vellum labels it inferred and records the proposal as its source. |
| Left out | Places where the assistant referred to something that does not exist. Vellum removed those parts. |
| Problems the trace check found | References or payloads that the trace engine refuses. |
| Questions the assistant could not answer | What the request left open. |

The assistant sees the parts of your model as a numbered table, without their real IDs. It can use only what is in that table. When it names something outside the table, Vellum leaves that out.

When the assistant asks for a part that the model already has, Vellum uses the existing part and says so.

## Decide

- **Accept and play** saves the flow as a scenario and plays it on the canvas.
- **Accept with these problems** saves a flow that has problems. Correct them in the scenario editor.
- **Reject** closes the proposal. Nothing is saved.

If the model changes after the assistant made the proposal, Vellum checks the proposal again and tells you. Decide again with the current check.

**Close** keeps the proposal undecided. A proposal costs tokens, so Vellum keeps it.

A scenario that came from a proposal records that origin.

## Ask for additions to the model

1. Select **Model** in the assistant panel.
2. Describe what to add. For example: "Add a notification service that listens to OrderCreated."
3. Select **Propose additions**.

The proposal opens on the canvas as a ghost layer. Each proposed element is a dashed card, and each proposed relationship is a dashed line. Vellum places them with the auto layout of **Tidy**, beside the cards of the current view. A proposed element inside a parent shows inside that parent when the view shows the parent open. The real cards do not move.

![The canvas with the ghost layer open. The bar at the top says Proposed additions, 22 pending, and has the buttons Accept all (22), Reject all and Close. Below the summary it says that 4 items are not drawn on this level. The real cards Customer, Notifications System, Orders System, Payments System and Web Application are solid. The proposed actor Ops Agent is a dashed card. The proposed systems Delivery System and Alerts System are dashed frames around dashed cards such as Courier, Delivery API and Alert Log. Each dashed card and line shows proposed, its alias and its confidence. Dashed lines join them to the real cards.](../../../assets/screenshots/assistant-ghost-layer.png)

Decide on the ghost layer:

- Point to a dashed card or line, or move the keyboard focus to it. Select the tick to accept the item, or the cross to reject it. Accepting an item also accepts what it needs. Rejecting an item also rejects the pending items that need it.
- Select a dashed card or line to read its rationale, confidence, alias and citation in the detail panel. The panel also lists what the item needs and what needs it.
- Select **Accept all** to accept every pending item in one decision. Vellum applies them in the order of what they need.
- Select **Reject all** to reject every pending item.
- Select **Close** to hide the layer. The proposal stays undecided.

The bar lists the items that the canvas does not draw on this level: items on another level, items inside a parent that the view shows closed, and operations, channels, subscriptions, bindings and messages. Each one has the same tick and cross. **Details** shows who made the proposal, its open questions and the parts that Vellum left out.

If Vellum refuses a decision, it applies nothing. The refused cards turn red and show the reason. Select **Deselect refused, accept the rest** to accept the rest of the decision. Select **Reject refused items** to reject the refused items and the items that need them.

An accepted card stays where its ghost was. Each accepted item is added with the label **inferred**. A proposal never changes or removes what is in the model.

The confidence is what the assistant claims. It is not a measurement.

## Find a proposal again

Select **Check** in the sidebar, then the **Review** tab, then the **Assistant** tab.

- **Your proposals** lists the flows and additions that the assistant made for you and that nobody decided. Select one to open it and decide. Additions open as the ghost layer. Select the bin to discard it. A discarded proposal is deleted.
- **History** lists every decided proposal of the project: the decision, who decided and when. Select one to read it. Additions open below the line as a list of items, each with its decision.

Additions are decided item by item. Each item records who accepted or rejected it, and when. A proposal with a decided item cannot be discarded. Reject its other items to close it.

Each list shows 20 proposals of each kind. Select **Show more** for older ones.

![The inner tab Assistant of the Review tab in the Check area. Below Stored answers, Your proposals says that no proposal waits for you. History lists one proposal: model, Notifications and an audit trail for orders, 1 accepted, 1 rejected, decided by Dev User on 17/10/2026 at 19:11:23, submitted by an agent.](../../../assets/screenshots/review-assistant-proposals.png)

## Ask for an explanation

1. Select an element on the canvas, or open a trace and go to an event.
2. Select **Explain** in the assistant panel.
3. Add a question if you want, and select **Explain**.

The answer appears as it is written. Select **Stop** to end it.

An explanation reads the declared model. It verifies nothing.

## Private repositories

Parts of the model that came from a private repository go to an AI provider only when a workspace owner allows it under **Settings → Privacy**. With that setting off, the assistant does not see those parts, and the proposal says how many it did not see.

## Limits

A project holds at most 50 undecided proposals. Accept, reject or discard old ones to make room. The assistant also limits how many requests one person and one project can make in a minute.

## Decide on a proposal from an agent

A coding agent with the `propose` scope can submit a flow or additions to the model. Vellum checks such a proposal as it checks a proposal of the assistant. A part that refers to an ID that names nothing is left out and listed. Nothing is in the model and no scenario is saved until you accept.

A proposal from an agent puts an entry in the inbox of the person who created the agent token. The bell in the top bar shows the count of unread entries.

1. Select the bell. The entry names the token, the number of additions and the proposal.
2. Select the entry. The project opens. Additions open as the ghost layer on the canvas. A flow opens in its dialog.
3. Accept or reject it, as you do for a proposal of the assistant.

The assistant panel also lists these proposals below **Waiting for you**. Each line shows the token that submitted it.

A part of the model that you accept from such a proposal has the label `inferred`. Its source names the token and the proposal. An agent token cannot accept or reject a proposal.

A project holds at most 50 undecided proposals. Reject or accept one to make room.
