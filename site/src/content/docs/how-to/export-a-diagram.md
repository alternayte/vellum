---
title: Export a diagram
description: Export a view as SVG, PNG or Mermaid, and the model as Structurizr DSL, for documents, slides and diagram-as-code tools.
---

This guide shows you how to put a Vellum diagram in a document, a slide or a diagram-as-code tool. You need the viewer role. An agent token needs the `read` scope.

## Select a format

| Format | Of | Use it when |
|---|---|---|
| SVG | One view | You put the diagram in a document or a web page. The picture scales without loss. |
| PNG | One view | The tool that receives the diagram takes images only, such as some slide tools. |
| Mermaid | One view | You keep the diagram as text in Markdown, for example in a README. |
| Structurizr DSL | The whole model | You use Structurizr or another tool that reads the C4 DSL. |

Each export is one-way. Vellum does not import these files. Each file lists what it drops. For the full lists, see [Diagram export formats](/reference/diagram-export-formats/).

The SVG shows what the canvas shows. The other formats keep less.

## Export a view in the app

1. Open the project.
2. Select **Project files** in the left toolbar.
3. Move the pointer onto the diagram. Select the **⋯** button.
4. Select **Export SVG**, **Export PNG** or **Export Mermaid**. The browser saves the file.

The app exports the main model with the saved layout of the view.

The browser makes the PNG from the SVG of the server, at twice the size of the SVG. The PNG uses the fonts of your computer when the fonts of Vellum are not installed. The same view can give a different PNG on a different computer. Use the SVG when you need the same file each time.

## Export the model as Structurizr DSL in the app

1. Select **Project files** in the left toolbar.
2. Below **Model document**, select **Export Structurizr DSL**. The browser saves `<project>.dsl`.

## Export with HTTP

Each endpoint takes the query parameter `revisionId`. Without it, the export reads the main model now. The commands use the session of [Use the HTTP API](/how-to/use-the-http-api/).

```sh
# The view as SVG, of the named revision.
curl -sS --fail-with-body -b vellum.cookies -o view.svg \
  "$VELLUM_URL/api/projects/$PROJECT/views/$VIEW/export/svg?revisionId=$REVISION"

# The view as Mermaid.
curl -sS --fail-with-body -b vellum.cookies -o view.mmd \
  "$VELLUM_URL/api/projects/$PROJECT/views/$VIEW/export/mermaid"

# The model as Structurizr DSL.
curl -sS --fail-with-body -b vellum.cookies -o workspace.dsl \
  "$VELLUM_URL/api/projects/$PROJECT/export/structurizr"
```

An unknown view answers `404`. An unknown revision answers `400`.

## Get the same SVG again

The SVG of a revision has the same bytes each time, while the layout of the view does not change. The layout of a view is not part of a revision. When somebody moves an element on the canvas, the next SVG of the same revision shows the new position.

To keep a diagram for a review or an audit, save the SVG file with the revision name.
