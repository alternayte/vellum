---
title: Promises and evidence
description: What a promise is, why a person approves it, what counts as evidence, and why a result is never more than what its check established.
---

This page explains the ideas behind promises and their evidence. The states, the kinds, the fields and the rules are in the reference: [Promises and evidence](/reference/promises-and-evidence/). The steps are in [Declare a promise](/how-to/declare-a-promise/).

## What a promise is

A model says what the parts of a system are. A scenario says what the parts do in one case. Neither says what must always be true.

A promise says that. It is one statement of behaviour that the system must keep, for example "an order is never charged two times". It has a requirement ID that never changes, such as `ORD-014`, a claim, the expected outcome, the forbidden outcome and links to the parts of the model that it is about.

A promise is a requirement of the design. It is not a description of the code, and it is not a test. It is what a test is for.

## Why a person approves it

A promise decides what the team must build. That is a decision about the product, with a cost. So a person takes it.

Only an owner who is signed in approves a promise. Nothing else approves: not an import, not an AI call, not an agent and not a check that passed. A promise that nobody approved is a draft. A draft is not a requirement, whoever wrote it and however good it is.

The approval binds exact content. The server computes a hash of everything that the approver read: the requirement ID, the revision, each field, each link and each obligation with its recipe. The approval names that hash. If the draft changed between the read and the approval, Vellum refuses the approval. A person cannot approve text that was changed after the person read it.

An approved revision does not change. To change a promise, a person approves a new revision. The earlier revision stays in the record.

## Why an agent cannot approve

A coding agent builds the system from the promises. If the agent could also approve a promise, it could approve the promise that its own code already passes. The requirement would then follow the implementation, and the promise would say nothing.

So an agent proposes. It can write a draft, and that draft has the origin `agent-suggested`, which a request cannot set or remove. It can start a draft revision of an approved promise. The approved revision stays in force until an owner approves the draft in the app.

No token scope gives approval. The rule is on the server and does not depend on what the agent was told. See [What an agent can and cannot do](/concepts/what-an-agent-can-and-cannot-do/).

## Obligations

A claim such as "never charged two times" cannot be checked as one thing. It is checked in parts. Each part is a proof obligation: one named check with a recipe, such as "play the checkout scenario with a timeout on the payment call and expect no second charge event".

An obligation has an ID of its own, such as `ORD-014/O2`. A test, a commit and an evidence record cite that ID. A result is thus about one obligation, not about the promise as a whole.

Obligations are part of what the approver approves. A promise with an easy obligation is a weaker promise than the same claim with a hard one, so the approver must see them.

Vellum has no single result for a promise. It reports each obligation, the counts and a list of gaps. Each gap is one sentence that names what is not established.

## What counts as evidence

Evidence is a result that Vellum established itself or read from the system that holds it. There are two kinds.

- **A deterministic check.** Vellum plays a scenario on the model and compares the events with the assertions, or looks for a pattern in the evidence of one repository scan. The same inputs give the same result.
- **External evidence.** A test ran outside Vellum, in GitHub Actions. Somebody records the run. Vellum then reads the run from GitHub and checks that it is the right repository, the right workflow, the right commit, completed, and that its conclusion is what the record says.

A statement is not evidence. When Vellum cannot read a run from the system that holds it, it stores the record as a claim, and the obligation stays `not-run`. This is true for a person and for an agent. An agent that says "the tests pass" changes no result.

Verified external evidence says one thing: Vellum read this run from GitHub. It does not say that the workflow executes the steps of the plan. Vellum does not run the test and cannot see what the test does.

## Why a generated test is not run

Vellum can generate obligations from the links of a draft, and an agent can write a test from an obligation. A new obligation has the result `not-run`.

The reason is simple: a test that exists has shown nothing. A test plan is a description of a check. It gets a result only when a run of it is bound as evidence. Until then the correct statement is that nobody ran it.

The same holds after a change. A run records a hash of the kind and the recipe of the obligation. When the obligation changes, the earlier run was a run of another check, and the obligation is `not-run` again.

Vellum also looks for a check that passes without testing anything. An assertion that "no charge event happens" holds in a scenario that has no charge step at all. Vellum marks such an assertion as vacuous. A check in which every assertion is vacuous is `unsupported`, not `passed`.

## Why a pass is not universal proof

A check is bounded. A scenario check plays one scenario, with the faults that it names, on one version of the model. A test in CI ran one set of inputs, on one commit, in one environment.

`passed` therefore means: this check held for the inputs that this run names. It does not mean that the promise holds for other inputs, other commits, other environments or the running system. There is no result "verified" and no result "proven". The four results are `passed`, `failed`, `not-run` and `unsupported`.

A scenario check has one more bound. It runs on the declared design, not on the code. It shows that the design keeps the promise if the code does what the design says. See [A trace is declared behaviour](/concepts/traces-are-declared/).

This is the same reason that a review finding is not proof and an estimate is not a measurement. See [Findings are not proof](/concepts/findings-are-not-proof/) and [Estimates are not measurements](/concepts/estimates-are-not-measurements/).

## Staleness and input fingerprints

A result is about the inputs that the check read. When an input changes, the result is about something that no longer exists.

So each run records what it read: the model stream and its version, the scenario version and its hash, each schema version, the repository and the commit. This record is the fingerprint of the inputs.

On every read, Vellum compares the fingerprint with the inputs as they are now. If all are the same, the result is current. If one moved, the result is stale, and Vellum names the input that moved. A stale pass counts as stale, not as passed.

Vellum does not carry a result forward and does not guess that a small change is harmless. A change to the model can break a promise, and only a new run shows if it did. The cost is that a change makes results stale and somebody runs the checks again. A deterministic check is cheap to run again. An external test needs a new run on the new commit.

Staleness also covers what the approver saw. The approval records the hash of each linked thing. When a linked scenario or contract changes after the approval, the evidence status lists a gap: the promise was approved against a design that has changed.

## How this reaches the build

Build Ready is the statement that a pinned state has no open point. An approved promise with a gap, a failed check, a stale result or no result is an open point, and so is a promise that nobody approved. Each is a blocker.

A blocker does not prevent an export. The SDD lists it, so that an agent does not treat an open point as settled. See [Bundle and SDD format](/reference/bundle-and-sdd-format/#build-ready-blockers) and [Stale results and Build Ready](/troubleshooting/stale-results-and-build-ready/).

## Related pages

- [Run the promise loop](/tutorials/promise-loop/)
- [Evidence and truth labels](/concepts/evidence-and-truth-labels/)
- [Connect a coding agent](/how-to/connect-a-coding-agent/)
