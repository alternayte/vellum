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
| Inferred | The assistant or a rule suggests it. The fact records the source and a confidence. | A person who accepts a suggestion. |
| Measured | A test or a run with recorded evidence shows it. | A recorded result. |
| Unknown | Nobody has established it. | A person. |

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

## Evidence without a place in the model

Some things that a scan finds have no place in the model yet: a dependency on a library, a data access, a deployment descriptor, and a pattern such as a transactional outbox. Vellum keeps them as evidence of the scan. Promises and checks can read them.
