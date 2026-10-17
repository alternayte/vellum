---
title: Model file mappings
description: What the Structurizr, Backstage and ADR readers keep and drop, the keys they give, and the truth labels of their facts.
---

The facts of a model file and of an ADR have the truth label `declared`. The facts of code and configuration have `observed-in-source`. A fact with evidence of both kinds is `observed-in-source`. Rules: `structurizr.dsl` 1, `structurizr.json` 1, `backstage.catalog` 1, `adr.nygard` 1, `adr.madr` 1.

## Structurizr

| Structurizr | Vellum |
|---|---|
| `person` | element of kind `actor` |
| `softwareSystem` | element of kind `system` |
| `container` | element of kind `app` |
| `container` with the tag `Database` | element of kind `store` |
| `component` | element of kind `component` |
| relationship | relationship, with its description as the label |
| relationship with the tag `Behavioural` | behavioural relationship |
| relationship with the tag `Derived` | nothing: it is a derived line of a Vellum view |

This is the reverse of the [Structurizr DSL export](/reference/diagram-export-formats/). An export of a model reads back as the same elements and relationships.

**Kept:** names, nesting, descriptions, the technology of a container or a component, the technology of a relationship, and the tags of an element. The DSL and the workspace JSON of one workspace give the same keys.

**Dropped:** groups and enterprises (their elements stay, with a note), views, styles, themes, deployment environments and nodes, properties, perspectives, URLs, custom elements, the default tags (`Element`, `Person`, `Software System`, `Container`, `Component`), the tags of a relationship except `Behavioural`, the technology of a person or a software system, implied relationships of the JSON, relationships with the tag `Derived`, and a component inside a database container (a note).

**Not followed, run or applied:** `!include`, `workspace extends`, `!script`, `!plugin`, `!docs`, `!adrs`, `!constant`, `!const`, `!var`, `!ref`, `!extend`, every other directive, `${NAME}` and every URL. Each gives a note.

**Keys:** `structurizr:element:<names>`, the names of the ancestors and the element joined with `/`; a `/` in a name is `%2F` and a `%` is `%25`. A relationship is `structurizr:relationship:<from>-><to>|<label>`. A DSL identifier resolves within its file only and is no part of a key.

## Backstage

| Backstage | Vellum |
|---|---|
| `System` | element of kind `system`; its `domain` is the tag `domain:<name>` |
| `Component` | element of kind `app` inside its `system` |
| `Component` with `subcomponentOf` | element of kind `component` inside that component |
| `Resource` | element of kind `store` inside its `system`; `spec.type` is its technology |
| `API` | element of kind `component` inside the one component that lists it in `providesApis`; `spec.type` is its technology |
| `Domain` | evidence only: the model has no level above a system |
| `dependsOn` | relationship "depends on" |
| `consumesApis` | relationship "consumes" to the API |

**Kept:** `metadata.title` (else `metadata.name`) as the name, `metadata.description`, `metadata.tags`, and `spec.type`, `spec.lifecycle` and `spec.owner` as observed attributes on the proposal.

**Dropped:** `Location` (not followed), `Group`, `User`, `Template`, links, annotations, labels, the API `definition`, and an API with no provider or with several (a note).

**Keys:** the entity ref in lower case, for example `component:default/orders`. The ref is the external key of the source binding of the element. A ref that leaves out the kind takes the default of the field; one that leaves out the namespace takes the namespace of the entity.

A component or a resource without a `system` has no place in the model. It stays evidence with the reason `no-parent-declared`.

## ADRs

| Field | Nygard | MADR |
|---|---|---|
| Title | the first `#` heading, without a leading number | the same |
| Status | `## Status` | front matter `status`, or the bullet `* Status:` |
| Date | `Date:` before the first section | front matter `date`, or the bullet `* Date:` |
| Context | `## Context` | `## Context and Problem Statement` |
| Decision | `## Decision` | `## Decision Outcome`, up to its first `###` heading |
| Consequences | `## Consequences` | `### Consequences`, or `### Positive Consequences` and `### Negative Consequences` |

Each ADR becomes a proposed decision with the origin `imported`. It cites the file from the title to its last line of text, and each section with its own lines. The status and the date of the file are shown as recorded; they do not decide the status in Vellum.

**Dropped:** considered options, pros and cons, decision drivers, deciders and links. A section is cut at 4,000 characters, with a note.

## Prose

Other Markdown and text files are no model files. Only the AI path reads them, when a person asks. Their proposals are `inferred` and cite their lines. See [Propose a model from design docs](/how-to/propose-from-design-docs/).
