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

Each export lists what it drops. Vellum does not import SVG, PNG or Mermaid. It reads a Structurizr DSL file back as declared facts: see [Import a Structurizr workspace or Backstage catalog](/how-to/import-a-model-file/). For the full lists, see [Diagram export formats](/reference/diagram-export-formats/).

The SVG shows what the canvas shows. The other formats keep less.

## Export a view in the app

1. Open the project.
2. Select **Project files** in the left toolbar.
3. Move the pointer onto the diagram. Select the **⋯** button.
4. Select **Export SVG**, **Export PNG** or **Export Mermaid**. The browser saves the file.

The app exports the model that the workbench shows, with the saved layout of the view. When the model switcher shows a named revision, the menu item reads **Export SVG of revision** and the name of the revision, and the file holds that revision. A draft or a variant is not exported: the app then exports main.

The browser makes the PNG from the SVG of the server, at twice the size of the SVG. The PNG uses the fonts of your computer when the fonts of Vellum are not installed. The same view can give a different PNG on a different computer. Use the SVG when you need the same file each time.

## Export the model as Structurizr DSL in the app

1. Select **Project files** in the left toolbar.
2. Below **Model document**, select **Export Structurizr DSL**. The browser saves `<project>.dsl`. When the workbench shows a named revision, the button names it and the file holds that revision.

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

The layout of a view is not part of a revision. Each export names its layout fingerprint, a hash of the view name, the root element and the saved positions. The same revision and the same layout fingerprint give the same bytes. When somebody moves an element on the canvas, the next SVG of the same revision shows the new position and has a new fingerprint.

To keep a diagram for a review or an audit, save the SVG file with the revision name.
