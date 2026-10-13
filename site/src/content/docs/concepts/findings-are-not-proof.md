---
title: Findings are not proof
description: What a finding of the review establishes, and what it cannot establish.
---

This page explains what a finding is evidence of.

A review rule reads the declared model: the elements, the contracts, the scenarios, the schemas and the promises. It reads no code and calls no service. It calls no AI.

## What "verified" means

The result **verified** means one thing: the declared model satisfies this structural rule. The text of each verified finding says so.

It does not mean that the running system behaves this way. It does not mean that the design is safe, secure, scalable or compliant. Vellum has no result that means that.

## What "unknown" means

A rule gives **unknown** when it cannot read a fact as stated:

- The model does not hold the fact. For example, the model has no field for idempotency.
- The fact is labelled inferred or unknown.
- The fact is marked stale.
- No scenario shows the behaviour that the rule reads. For example, no scenario publishes to the channel.

A rule never reads an inferred fact as a fact. It never gives "verified" for a fact it could not read, and it never gives "concern" for one. One rule is different: TRU-001 reads the label itself, and it gives "concern" for an inferred, unknown or stale fact on the path of an approved promise. See [Evidence and truth labels](/concepts/evidence-and-truth-labels/).

An unknown is a gap that stays visible. It is not a pass. It also does not block Build Ready: only a concern with the severity `blocking` does.

## Why there is no score

One number hides which rule fired, for which part, and why. A person cannot check a number against the model.

A finding names its rule, its subject, the parts it cites and one sentence of reason. You can check each one against the model. The hash of the findings is the same for the same model, scenarios, schemas and promises. [Review rules](/reference/review-rules/) lists each rule and each result.

## What a decision does

A decision does not delete a finding. A settled finding stays in the list, with the decision that answers it. The model still has the property that the rule found. The decision says why that is acceptable.

Only an accepted decision settles a finding, and only an owner accepts a decision. A proposed, rejected or superseded decision settles nothing.

A decision settles the exact finding that a person read. When the facts of the finding change, or when the rule has a new version, the finding needs a new review. Until then the decision settles nothing, and a blocking finding blocks again.

The answer **Won't fix** is not a decision. It needs a reason, and it never removes a blocker.

A decision does not change a truth label. A decision that settles a finding about an inferred fact does not make the fact confirmed. The Build blocker of the fact stays until a person confirms the fact.

## Rules that argue for less

Some rules look for a missing answer to failure. Other rules look for a service, a queue or a part that the model gives no reason for.

A concern of the second kind is a question, not a defect. "What does the queue buy here?" can have a good answer. Write the answer in a decision.

## What the assistant adds

The assistant gives suggestions. Vellum checks each one on the server:

- A suggestion must name parts that are in the data of the request.
- A risk is shown as a risk only when a finding of a rule supports it. Every other risk is shown as a question, with the note that the model does not show it.
- Vellum checks that the rule fired. It does not check what the sentence of the assistant says. So a risk shows the sentence of the finding beside the sentence of the assistant.
- A suggestion that states an assurance is removed.

Nothing from the assistant becomes a finding, an answer to a finding or an accepted decision. A person does that.

An answer of the assistant is not evidence. Evidence for a promise comes from checks and from test runs. See [Promises and evidence](/concepts/promises-and-evidence/) and [A trace is declared behaviour](/concepts/traces-are-declared/).
