---
status: accepted
date: 2023-05-04
deciders: Ana, Ben
---
# Use PostgreSQL for orders

## Context and Problem Statement

Orders need transactions across order lines.
Which database do we use?

## Considered Options

* PostgreSQL
* MongoDB

## Decision Outcome

Chosen option: "PostgreSQL", because it gives transactions.

### Consequences

* Good, because the outbox lives in the same transaction.
* Bad, because we run one more server.

## Pros and Cons of the Options

### PostgreSQL

* Good, because transactions
