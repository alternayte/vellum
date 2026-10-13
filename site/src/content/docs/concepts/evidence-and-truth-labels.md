---
title: Evidence and truth labels
description: What each label on a fact means, and what Vellum does when evidence changes.
---

This page explains the label on each fact in a model and the rules Vellum follows for evidence.

## The five labels

| Label | Meaning | Who sets it |
|---|---|---|
| Declared | A person states that this is so. | A person who adds or edits the fact. |
| Observed in source | Vellum read it in a repository at a commit that the fact records. | An import, when a person accepts its proposal. |
| Inferred | The assistant suggests it. The fact records the source and a confidence from 0 to 1. | Vellum, when a person accepts a suggestion of the assistant. |
| Measured | A test or a run shows it. The fact records a pointer to the evidence. | A person who edits the fact and gives the pointer. |
| Unknown | Nobody has established it. | A person. |

The API gives the labels as `declared`, `observed-in-source`, `inferred`, `measured` and `unknown`, in the field `factStatus`.

Vellum does not set the label **Measured** on a fact of the model. A k6 result and a check of a promise are evidence of their own kind. They do not change a label. See [Estimates are not measurements](/concepts/estimates-are-not-measurements/).

The canvas shows a marker on every fact that is not declared. Search finds facts by label.

## What a label does not say

**Observed in source** says that the text is in the repository at that commit. It does not say that the code runs, that a route answers, or how fast it is.

**Inferred** with a confidence of 90% says how sure the assistant claims to be. It is not a measurement.

A trace says what the declared design does. It is not a label on any fact.

## Rules Vellum follows

1. **Vellum never raises a label by itself.** An inferred fact stays inferred until a person changes it.
2. **An import never overwrites what a person set.** A difference becomes a conflict that shows both values.
3. **Absence counts only when Vellum looked.** A scan can say that evidence is gone only for a fact whose source files it read, on the branch that the model follows, with the same extraction rules.
4. **A stale fact is not a deleted fact.** The mark says that the last scan that looked did not find the evidence. The fact stays until a person accepts its removal.
5. **A change of extraction rules is not a change of the code.** After an update of the rules, Vellum first matches the facts it has to the facts it now finds. It asks you when it cannot match one exactly.
6. **Every accepted fact records who accepted it and when.**

## What a label changes

A review rule reads a fact as stated only when the fact is declared, observed in source or measured, and is not stale. For each other fact the rule gives the result `unknown`. See [Findings are not proof](/concepts/findings-are-not-proof/) and [Review rules](/reference/review-rules/).

A fact that is inferred, unknown or stale prevents Build Ready. The Build manifest lists it as `fact-inferred`, `fact-unknown` or `fact-stale`. It does not prevent a save or an export.

## Evidence without a place in the model

Some things that a scan finds have no place in the model yet: a dependency on a library, a data access, a deployment descriptor, and a pattern such as a transactional outbox. Vellum keeps them as evidence of the scan. A check of a promise can read them: a `source-pattern-check` looks for the pattern `transactional-outbox` or `in-memory-outbox` in the evidence of one scan. See [Extraction rules](/reference/extraction-rules/) and [Promises and evidence](/concepts/promises-and-evidence/).
