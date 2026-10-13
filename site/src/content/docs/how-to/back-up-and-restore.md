---
title: Back up and restore
description: Keep a copy of the model of one project as a file, put it back, and know when you need a backup of the whole instance.
---

This guide shows you how to keep a copy of the model of one project, and how to put that copy back. You need the viewer role to export and the editor role to import.

Vellum has two kinds of copy. Select the one that fits your goal.

| Goal | Use |
|---|---|
| Keep the model of one project in a file, or in your own repository | The model document of this guide |
| Recover a complete instance after a failure: every project, scenario, promise, setting, account and credential | [Back up and restore](/runbooks/backup-and-restore/) in the runbooks. The operator of the instance does it. |

The model document is not a backup of a project. It holds the elements, the relationships, the messages, the schemas and the contracts, with their truth labels. It does not hold scenarios, promises, evidence, views, decisions, cost sheets or project documents. [Model document format](/reference/model-document-format/) lists what it holds.

The app has no button for the model document. Use the HTTP API. [Use the HTTP API](/how-to/use-the-http-api/) shows how to sign in and get the cookie file `vellum.cookies`.

## Export the model

1. Find the ID of the project. It is the last part of the address of the project in the app: `/projects/<project-id>`.
2. Export the model as JSON.

   ```sh
   curl -sS --fail-with-body -b vellum.cookies -o model.json "$VELLUM_URL/api/projects/<project-id>/export"
   ```

3. To get YAML, add `?format=yaml`.

   ```sh
   curl -sS --fail-with-body -b vellum.cookies -o model.yaml "$VELLUM_URL/api/projects/<project-id>/export?format=yaml"
   ```

The export orders each list by ID. The same model always gives the same file, so the file works well under version control.

The export reads the main model. It does not read a draft or a variant.

## Keep the scenarios

The model document has no scenarios. To keep them, save each scenario document.

```sh
curl -sS --fail-with-body -b vellum.cookies "$VELLUM_URL/api/projects/<project-id>/scenarios" | jq -r '.items[].id' |
  while read -r id; do
    curl -sS --fail-with-body -b vellum.cookies -o "scenario-$id.json" "$VELLUM_URL/api/projects/<project-id>/scenarios/$id"
  done
```

## Put the model back

Import the file into the project that it came from.

```sh
curl -sS --fail-with-body -b vellum.cookies -H 'content-type: application/json' -d @model.json "$VELLUM_URL/api/projects/<project-id>/import"
```

For a YAML file, send the content type `application/yaml` and use `--data-binary` in place of `-d`.

The answer is `200` with an empty body. The import is one transaction: on an error, Vellum writes nothing and the answer names the first problem.

Know what an import does before you use it:

- **It adds and it changes. It removes nothing.** A fact that is in the model and not in the file stays in the model. Remove it by hand.
- **It writes to the main model directly.** It makes no proposal and asks for no review.
- **It keeps IDs.** A fact with an ID that exists in the project is changed. A fact with a new ID is added.

## Limits

- **Another project of the same instance.** A schema ID belongs to one project of an instance. An import of a file with schemas into a second project of the same instance is refused with a message that the schema belongs to another project. A model with no schemas imports into any project.
- **Another instance.** The import works when the instance holds none of the IDs of the file. The repository directory `examples/orders` holds a model document that you can import in this way.
- **Parent order.** The import adds elements in the order of the file, and the export orders them by ID. In an empty project, a child whose ID sorts before the ID of its parent is refused with "Parent element not found". Move the parent before the child in the file.
- **Size.** A file has 16 MB at most.

## See also

- [Model document format](/reference/model-document-format/)
- [Export for an agent](/how-to/export-for-an-agent/) gives the bundle and `SDD.md`, which are for a coding agent and cannot be imported.
- [Save revisions and compare variants](/how-to/compare-variants/)
