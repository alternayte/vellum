---
title: Views, levels and derived lines
description: How a diagram shows one level of the model, and why a relationship between hidden parts draws as a derived line between the cards that hold them.
---

This page explains what a diagram shows at each level, and what a derived line is.

## A view shows one level

A view is a diagram of the model. It shows one level at a time.

- The top level shows the elements that have no parent: actors and systems.
- When you drill into an element, the level shows the children of that element inside a frame.
- When you expand a card, its children show inside it on the same level. A collapsed card hides them.

A card is visible when it is on the level, or when it is a child of an expanded card. The elements inside a collapsed card are hidden.

## A relationship always shows

A relationship joins two elements at any depth. A view draws each relationship that has at least one end inside the level:

| Ends | What the view draws |
|---|---|
| Both ends are visible cards | The relationship as itself: a direct line. |
| An end is hidden inside a visible card | The end attaches to the nearest visible card that holds it. The relationship is part of a derived line. |
| Both ends attach to the same card | Nothing. A line from a card to itself has no meaning at this level. |
| An end is outside the level | Nothing. The view does not show that end. |

## Derived lines

A derived line joins two visible cards for the relationships between the parts hidden inside them. All such relationships between the same two cards, in one direction, merge into one derived line. A line from A to B and a line from B to A are two lines.

- A derived line with one relationship shows the label of that relationship.
- A derived line with more relationships shows "N relationships", for example "3 relationships".
- A derived line is thinner and lighter than a relationship. Its label has a dashed border and a count badge with the number of relationships.

Select a derived line, or its label, to list its relationships in the detail panel. Select an entry to open that relationship.

A derived line is not a fact. Vellum computes it again from the model each time the view changes. It is never stored, it has no truth label, and nobody accepts it. A change to the model, a collapse or an expand changes the derived lines at once.

A derived line has no saved sides. Each end attaches to the side that faces the other card. You cannot drag its ends, delete it or change its kind. To change a fact under it, open the relationship.

## Exports and share links

The SVG, PNG, Mermaid and Structurizr DSL exports draw the same derived lines as the canvas with every card collapsed. The rules are the same in the canvas and on the server. See [Diagram export formats](/reference/diagram-export-formats/#derived-lines).

A share link draws the derived lines of the facts that it shares. A view link shares the elements that the view places, with their parents. A relationship with an end that the view does not place is not shared, so a view link does not draw a derived line for it. See [Share and embed a read-only page](/how-to/share-and-embed/).

## Proposals

The ghost layer of an assistant proposal does not draw derived lines. A pending relationship is a decision of its own: it shows as a dashed ghost line when both of its ends are on the canvas. When an end is hidden inside a collapsed card, the bar of the layer lists it as not on this level. Expand the card to see the ghost line. A derived line includes only the relationships of the model, never a pending proposal.
