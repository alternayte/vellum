---
title: Cost model
description: Resources, assumptions, price entries, the estimate rules, the unknown reasons, comparison and the HTTP endpoints.
---

This page describes the data of a cost estimate and the rules that compute it.

## Cost sheet

Each model branch of a project has one cost sheet: main, each draft and each variant. A sheet holds resources and assumptions.

- A new variant starts with a copy of a cost sheet. Vellum makes the variant and the copy in one transaction. The default source is the branch that the model is forked from. `costSheetFrom` in the request names another branch.
- A new draft has an empty sheet until you copy one into it.
- A copy leaves out a resource whose element is not in the model of the new branch.
- A copy keeps each assumption. When the subject of an assumption is not in the model of the new branch, the copy has no subject.
- A branch whose source has no sheet starts with no sheet.

### Resource

| Field | Content |
|---|---|
| `elementId` | The model element that the resource runs. The element must be in the model of the branch. |
| `name` | Unique for the element. One line of at most 200 characters. |
| `category` | `compute`, `database`, `queue`, `storage`, `egress` or `other`. |
| `provider` | A name of 1 to 40 letters, digits, spaces, dots, hyphens and underscores that starts with a letter or a digit, for example `aws` or `hetzner`. A price applies when its provider is the same, in any case. |
| `region`, `sku` | Text. The region has at most 100 characters. The SKU has at most 200 characters. |
| `quantity`, `unit` | The baseline for one month. Leave both out for a resource that has usage lines only. The quantity is from 0 to 1,000,000,000,000 with at most 6 decimal places. |
| `priceRef` | The ID of a price entry of the workspace, or null. A resource without a quantity takes no `priceRef`. |
| `usage` | Up to 20 usage lines. Each has an `id`, a `name`, an `assumptionId` of the same sheet and a `priceRef` or null. |
| `factStatus`, `provenance` | The truth label of the resource, as for other model facts. The default is `declared`. |

### Assumption

| Field | Content |
|---|---|
| `name` | Unique in the sheet. |
| `kind` | `requests`, `storage`, `egress`, `messages` or `other`. |
| `subjectId` | An element, an operation or a channel of the model, or null for the whole system. |
| `value`, `unit` | The number and its unit. The value is from 0 to 1,000,000,000,000 with at most 6 decimal places. |
| `label` | `declared`, `measured` or `inferred`. |
| `source` | Where the number comes from. Necessary for `measured` and `inferred`. At most 2000 characters. |
| `effectiveDate` | The first day on which the number applies. |

A sheet holds at most 500 resources and 500 assumptions.

A request can give the `id` of a new resource, a new usage line or a new assumption. Without it, Vellum makes one. Each write answers with the whole sheet: `branchId`, `version`, `resources` and `assumptions`. Each resource in the answer has `elementName`. It is null when the element is not in the model of the branch.

## Price entry

The price book belongs to the workspace. It holds at most 2000 entries. An entry never changes.

| Field | Content |
|---|---|
| `provider`, `region`, `sku` | What the price is for. The provider is `azure`, `aws`, `gcp` or `custom`. A custom price needs a `sku`. The region can be empty. |
| `description` | Text, or null. |
| `unit`, `unitPrice`, `currency` | The price for one unit. The price is from 0 to 1,000,000 with at most 10 decimal places. The currency is a code of three upper-case letters, for example `USD`. |
| `effectiveDate` | The first day on which the price applies. |
| `source` | `custom` or `fetched`. |
| `acceptedBy`, `acceptedAt` | The person who entered the price, or who picked the fetched row, and the time. |
| `fetched` | For a fetched price: `url`, `fetchedAt`, `meterId` and `providerEffectiveDate`. Null for a custom price. |
| `unitReadable` | False when Vellum does not read the unit. A line with this price is then unknown. |

A project can pin an entry. A project pins at most 500 entries. An owner cannot delete a pinned entry: the delete answers `409`. When an entry is deleted, each line that names it becomes unknown with the reason `price-not-found`.

## Azure Retail Prices

Vellum reads the Azure Retail Prices API as Microsoft documents it at <https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices>. The page was read on 2026-10-13.

- The request is a `GET` of the path `api/retail/prices` under the base URL, with `api-version=2023-01-01-preview`, `currencyCode` and `$filter`. The currency code is in single quotes, for example `currencyCode='USD'`. Without `currency` in the search, Vellum uses the currency of the workspace.
- The filter uses `serviceName`, `armRegionName`, `skuName`, `meterId` and `skuId` with `eq`, `contains(meterName, '…')` and `priceType eq 'Consumption'`.
- A search needs `serviceName`. The values are case sensitive.
- Vellum refuses a search value that holds a single quote or a control character, that starts or ends with a space, or that has more than 100 characters.
- The response holds `Items` and `NextPageLink`. Vellum reads at most 2 pages and shows at most 500 rows. It follows `NextPageLink` only to the server in the settings. `more` in the answer is true when the API has more rows than Vellum read.
- Vellum does not show a row without a meter ID, a unit or a price, or a row in another currency than the search.
- Vellum stores `retailPrice`, `unitOfMeasure`, `currencyCode`, `meterId`, `armRegionName`, `skuName`, `productName`, `meterName` and `effectiveStartDate`.
- Vellum does not store a row whose `type` is not `Consumption`, a row with a `reservationTerm` or a row whose price is outside the range of a unit price. Such a row has a sentence in `notImportable`.
- The stored `sku` is the `skuName`, or the `meterName` when the row has no `skuName`. The stored effective date is `effectiveStartDate`, or the day of the request when the row has none.
- The API returns one row for each price tier of a meter. The rows have the same meter, SKU and region and different `tierMinimumUnits`. When one row of such a group has `tierMinimumUnits` above 0, Vellum stores no row of the group. The row from 0 is the price of the first units only, and it is often 0. The import answers `409` with the type `price_tiered`. Enter the price that you pay as a custom price.

The API is public. Vellum sends no credential and no workspace data to it, except the search values. The base URL is the setting `cost.azureRetailPricesBaseUrl`. The instance policy for outbound addresses applies to it. An empty value turns the search off.

The search and the import answer with these refusals:

| Status | Type | Cause |
|---|---|---|
| `400` | `price_source_refused` | The API refused the filter. |
| `409` | `price_source_not_configured` | The setting is empty, or the instance policy refuses the address. |
| `409` | `price_tiered` | The import names a meter with price tiers. |
| `409` | `conflict` | The import finds no row that Vellum can store, or more than one row. |
| `502` | `price_source_unavailable` | The API gave no usable answer. |
| `503` | `price_source_rate_limited` | The API limits the requests of the server. |

## Units

A unit is `[number] name [/ period]`. The period is `hour`, `day` or `month`. Examples: `1 Hour`, `GB/month`, `1000000 requests`, `instances`. A space stands between the number and the name. Names are compared in lower case and in the singular. A unit has at most 50 characters. The number is from 0.001 to 1,000,000,000. `h`, `hr` and `hrs` are hours, `d` is a day and `mo` is a month.

Vellum does not read a number with a suffix. A unit such as `10K requests` is not a unit. Write `10000 requests`.

Vellum converts a quantity to price units with these rules only:

| Quantity | Price | Conversion |
|---|---|---|
| A time, for example `730 hours` | For a time, for example `1 Hour` | A day is 24 hours. A month is 730 hours. |
| `hours/day` | For a time | The hours on each day, times 730 ÷ 24 days. |
| A name with no period, or per month | For the same name, with no period or per month | The quantity is the amount of one month. |
| A name per hour or per day | For the same name, with no period | Times 730 hours, or times 730 ÷ 24 days. |
| A name per hour or per day, for a flow | For the same name per month | Times 730 hours, or times 730 ÷ 24 days. |
| A name per hour or per day, not for a flow | For the same name per month | No conversion. The line is unknown. |
| A name with no period | For the same name per hour or per day | Times 730 hours, or times 730 ÷ 24 days. |
| `instances` | For a time | Each instance runs for 730 hours. |

A month is 730 hours in each estimate: 8760 hours of a year, divided by 12.

All other pairs do not convert. The line is then unknown.

### Flows and held amounts

A price for a name per month, for example `1 GB/Month`, is a price for an amount that is held for one month. Stored data is such an amount. `5 GB/day` of new data is not 152 GB that are held for the month. Thus Vellum does not multiply a quantity per hour or per day against a price per month, and the line is unknown with the reason `unit-mismatch`.

The rule has one exception. A flow is an amount that passes in a period. For a flow, the amount of a month is the sum of its days, and Vellum multiplies. A line is a flow in these cases:

- The category of the resource is `egress`.
- The line is a usage line and the kind of its assumption is `requests`, `egress` or `messages`.

For a held amount, enter the amount that one month holds, with no period or per month.

## Estimate

The estimate is a function of the resources, the assumptions, the price entries, the date, the currency of the workspace and the stale age. The same inputs give the same result and the same hash.

- A baseline line is quantity × unit price, after the unit conversion.
- A usage line is assumption × unit price, after the unit conversion.
- All arithmetic is decimal.
- The amount of a line is rounded to 4 decimal places, half away from zero.
- A subtotal or a total is the sum of rounded line amounts. It is not rounded again.
- There is one total for each currency. Vellum converts no currency.
- Each line holds the ID of its price entry and, for a usage line, the ID of its assumption.
- Each line holds what its price is for: the provider, the region, the SKU, the meter ID of the provider and the source. A saved estimate keeps these fields.
- A price is used only for a resource of the same provider. When the resource and the price each name a region, the regions must be the same. Vellum compares regions without regard to upper and lower case. A price with no region applies to each region of its provider.
- A resource is priced only while its element is in the model of the branch.
- The amount of one line is at most 1,000,000,000,000,000.
- The default date is the current day in UTC.
- The answer holds `branchId`, `modelVersion`, `sheetVersion`, `inputsFingerprint`, `hash` and `estimate`. `inputsFingerprint` is the hash of the inputs without the date.
- Dates and numbers in the result have the same text on each server. The language settings of the server do not change the hash.
- `hash` is the SHA-256 of the canonical JSON of the estimate.
- `engine` is `cost-2`. Engine `cost-1` did not compare the provider and the region of a price and did not read the elements of the model.

### Unknown reasons

A line with one of these reasons has no amount. It is in no subtotal and in no total.

| Reason | Meaning |
|---|---|
| `no-price` | The line names no price entry, or the resource has no quantity and no usage line. |
| `price-not-found` | The price entry is not in the price book of the workspace. |
| `price-not-effective` | The effective date of the price is after the date of the estimate. |
| `unit-mismatch` | Vellum does not read a unit, or the two units do not convert. |
| `missing-assumption` | The assumption of the usage line is not in the sheet. |
| `assumption-not-effective` | The effective date of the assumption is after the date of the estimate. |
| `element-not-in-model` | The element of the resource is not in the model of the branch. Each line of the resource has this reason. |
| `price-provider-mismatch` | The price entry is for another provider than the resource. |
| `price-region-mismatch` | The resource and the price entry each name a region, and the regions are different. |
| `amount-out-of-range` | The amount is larger than the largest amount of one line, or the calculation does not fit in a decimal number. |

### Flags

A line with a flag has an amount, and the amount is in its total.

| Flag | Meaning |
|---|---|
| `stale-price` | The price was entered or fetched more than `cost.stalePriceAfterDays` days before the date of the estimate. The default is 90. |
| `currency-mismatch` | The price is not in the currency of the workspace. The amount is in the total of its own currency. |

The age of a price counts from the day it was entered or fetched. It does not count from the effective date of the provider, because a provider can keep a price for years.

## Saved estimate

A saved estimate holds a name, the branch, the version of the model stream, the date, the fingerprint of the inputs, the hash, the user, the time and the result. The default name is `Estimate` and the date. A list of saved estimates leaves out the result. It never changes. When the inputs of the branch change, the estimate is `outdated` and `changes` lists each resource, assumption, price or setting that was added, removed or changed. A branch holds at most 100 saved estimates.

- A resource counts as changed when its element leaves the model.
- An estimate that an earlier engine saved is `outdated`. `changes` then has an entry of the kind `engine`. It lists the inputs that were added or removed. It cannot tell if an input that is on both sides changed.

## Comparison

A comparison matches the resources of two branches by element and name. For each total, category and resource it gives the left amount, the right amount and the difference for each currency. The difference is right minus left. A row with an unknown line on either side has no difference.

`comparable` is true only when both sides have at least one line, no line is unknown and all amounts are in one currency. Only then does the comparison give `cheaper` and `difference`. The other cases give one of these reasons:

| Reason | Meaning |
|---|---|
| `left-has-no-lines`, `right-has-no-lines`, `both-sides-have-no-lines` | The cost sheet of the side is empty. An empty sheet is not a cost of zero. No row of the comparison has a difference. |
| `left-has-unknown-lines`, `right-has-unknown-lines`, `both-sides-have-unknown-lines` | The side has a line with no amount. |
| `several-currencies` | The amounts are in more than one currency. |

## Merge and close of a draft

The merge of a draft merges its cost sheet into the sheet of main. The model changes and the sheet changes are in one transaction. Either all of them are stored, or none.

The merge is a three-way merge by the ID of each resource and each assumption. The common ancestor is the sheet as the draft copied it from main. A draft sheet that is not a copy of the sheet of main has an empty ancestor.

| Draft | Main | Result on main |
|---|---|---|
| Not changed | Changed, added or removed | The value of main. |
| Changed, added or removed | Not changed | The value of the draft. |
| Changed | Changed to the same value | That value. |
| Changed | Changed to a different value, or removed | Conflict. |
| Added | Added with the same ID and a different value | Conflict. |
| Removed | Changed | Conflict. |

These cases are conflicts too:

- The merged sheet has two resources with the same name for one element.
- The merged sheet has two assumptions with the same name.
- The merged sheet has more than 500 resources or 500 assumptions.

Vellum refuses a merge with a conflict. The answer is `409` with the type `cost_sheet_conflict` and one text that names at most 10 conflicts and the number of the others. Nothing is stored. The merge of the model takes a resolution for each model conflict. A sheet conflict has no resolution: change the sheet of the draft or of main until the two agree, then merge again.

`POST /api/projects/{projectId}/drafts/{draftId}/merge/preview` returns `costSheet` with the number of sheet `changes` and the list of `conflicts`.

A merged resource can name an element that the merged model does not have. Its lines are then unknown with the reason `element-not-in-model`.

When a draft is merged or abandoned, Vellum deletes its cost sheet and its saved estimates in the same transaction. A variant is not closed, so it keeps its sheet.

## Audit

Vellum writes an entry to the audit log of the workspace for each change of the price book. The entry commits with the change.

| Action | When |
|---|---|
| `price.added` | An owner adds a custom price. |
| `price.imported` | An owner stores a row from Azure. |
| `price.deleted` | An owner deletes an entry. |

The target of the entry holds the ID of the price entry, the provider, the SKU, the region, the unit price, the currency, the unit and, for a fetched price, the meter ID. A price is not a secret.

## Endpoints

A project endpoint takes `?branchId=` with the stream ID of a draft or a variant. Without it, the endpoint uses main. A viewer reads. An editor writes and pins. Only an owner of the workspace changes the price book or searches Azure.

| Method and path | Purpose |
|---|---|
| `GET /api/projects/{projectId}/costs` | The cost sheet. |
| `POST /api/projects/{projectId}/costs/resources` | Add a resource. |
| `PUT`, `DELETE /api/projects/{projectId}/costs/resources/{resourceId}` | Change or remove a resource. |
| `POST /api/projects/{projectId}/costs/assumptions` | Add an assumption. |
| `PUT`, `DELETE /api/projects/{projectId}/costs/assumptions/{assumptionId}` | Change or remove an assumption. |
| `POST /api/projects/{projectId}/costs/copy` | Replace the sheet with a copy of the sheet of the branch in `from`. `from` is `main` or a branch ID, and it is another branch than the target. |
| `POST /api/projects/{projectId}/variants` | Create a variant. `costSheetFrom` is `main` or a branch ID. The variant starts with a copy of that cost sheet. |
| `GET /api/projects/{projectId}/costs/estimate` | The estimate as it is now. Nothing is stored. `asOf` is the date of the estimate. |
| `GET`, `POST /api/projects/{projectId}/costs/estimates` | List the saved estimates, newest first, or save one. The body of a save has `name` and `asOf`. Both are optional. |
| `GET`, `DELETE /api/projects/{projectId}/costs/estimates/{estimateId}` | Read or delete a saved estimate. |
| `GET /api/projects/{projectId}/costs/compare` | Compare the branches in `left` and `right` at the date in `asOf`. A side is `main` or a branch ID. |
| `GET /api/projects/{projectId}/costs/prices` | The price book of the workspace of the project. |
| `GET`, `POST /api/workspaces/{workspaceId}/prices` | List the price book, or add a custom price. The list is in pages: `limit` is from 1 to 200 with a default of 50, and `cursor` is the `cursor` of the page before. Each entry has `usedBy`: the projects that use it. |
| `DELETE /api/workspaces/{workspaceId}/prices/{priceId}` | Delete an entry that no project uses. A used entry gets `409` with the type `price_in_use` and the names of the projects. |
| `POST /api/workspaces/{workspaceId}/prices/csv` | Import custom prices from the CSV text in `csv`. All lines or none. An error names `line N` or `line N.column`. |
| `GET /api/workspaces/{workspaceId}/prices/aws/services` | The AWS services, by offer code. |
| `GET /api/workspaces/{workspaceId}/prices/aws/regions` | The regions of the AWS service in `service`. |
| `GET /api/workspaces/{workspaceId}/prices/aws/prices` | The on-demand prices of `service` in `region` whose product holds each word of `search`. |
| `POST /api/workspaces/{workspaceId}/prices/aws` | Store the rows with `service`, `region` and `rateCodes` (1 to 50). Vellum reads the price file again. |
| `GET /api/workspaces/{workspaceId}/prices/gcp/services` | The Google Cloud services. Needs the secret `gcp:apiKey`. |
| `GET /api/workspaces/{workspaceId}/prices/gcp/skus` | The SKUs of `serviceId` in `region` whose text holds each word of `search`, priced in `currency`. |
| `POST /api/workspaces/{workspaceId}/prices/gcp` | Store the SKUs with `serviceId`, `region`, `currency` and `skuIds` (1 to 50). Vellum reads the catalog again. |
| `GET /api/workspaces/{workspaceId}/prices/azure/meters` | Search Azure meters. Parameters: `serviceName`, `region`, `skuName`, `meterName`, `currency`. |
| `POST /api/workspaces/{workspaceId}/prices/azure` | Store the row with `meterId`, `skuId`, `armRegionName` and `currency`. Vellum reads the row again. |

A workspace makes at most 30 requests to the Azure, AWS and Google Cloud endpoints in one minute. A request over the limit gets `429` with the type `rate_limited` and a `Retry-After` header.

A request body without `value` for an assumption, or without `unitPrice` for a custom price, gets `400` with the name of the field. Vellum does not read a missing number as 0.

When a project is deleted, Vellum deletes its cost sheets and its saved estimates. The price book stays with the workspace. The prices that only this project used can then be deleted.

See [Estimate costs](/how-to/estimate-costs/) for the procedure and [An estimate is not a measurement](/concepts/estimates-are-not-measurements/) for what a cost estimate can show.
