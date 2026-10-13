---
title: Cost model
description: Resources, assumptions, price entries, the estimate rules, the unknown reasons, comparison and the HTTP endpoints.
---

This page describes the data of a cost estimate and the rules that compute it.

## Cost sheet

Each model branch of a project has one cost sheet: main, each draft and each variant. A sheet holds resources and assumptions. A new draft or variant has an empty sheet until you copy one into it.

### Resource

| Field | Content |
|---|---|
| `elementId` | The model element that the resource runs. The element must be in the model of the branch. |
| `name` | Unique for the element. |
| `category` | `compute`, `database`, `queue`, `storage`, `egress` or `other`. |
| `provider` | `azure`, `aws`, `gcp` or `custom`. |
| `region`, `sku` | Text. |
| `quantity`, `unit` | The baseline for one month. Leave both out for a resource that has usage lines only. |
| `priceRef` | The ID of a price entry of the workspace, or null. |
| `usage` | Up to 20 usage lines. Each has a `name`, an `assumptionId` and a `priceRef`. |
| `factStatus`, `provenance` | The truth label of the resource, as for other model facts. The default is `declared`. |

### Assumption

| Field | Content |
|---|---|
| `name` | Unique in the sheet. |
| `kind` | `requests`, `storage`, `egress`, `messages` or `other`. |
| `subjectId` | An element, an operation or a channel of the model, or null for the whole system. |
| `value`, `unit` | The number and its unit. |
| `label` | `declared`, `measured` or `inferred`. |
| `source` | Where the number comes from. Necessary for `measured` and `inferred`. |
| `effectiveDate` | The first day on which the number applies. |

A sheet holds at most 500 resources and 500 assumptions.

## Price entry

The price book belongs to the workspace. It holds at most 2000 entries. An entry never changes.

| Field | Content |
|---|---|
| `provider`, `region`, `sku` | What the price is for. |
| `unit`, `unitPrice`, `currency` | The price for one unit. The price has at most 10 decimal places. |
| `effectiveDate` | The first day on which the price applies. |
| `source` | `custom` or `fetched`. |
| `acceptedBy`, `acceptedAt` | The person who entered the price, or who picked the fetched row, and the time. |
| `fetched` | For a fetched price: the address, the time of the request, the meter ID of the provider and the effective date of the provider. |

A project can pin an entry. An owner cannot delete a pinned entry. When an entry is deleted, each line that names it becomes unknown.

## Azure Retail Prices

Vellum reads the Azure Retail Prices API as Microsoft documents it at <https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices>. The page was read on 2026-10-13.

- The request is `GET {base URL}/api/retail/prices` with `api-version=2023-01-01-preview`, `currencyCode='USD'` and `$filter`.
- The filter uses `serviceName`, `armRegionName`, `skuName`, `meterId` and `skuId` with `eq`, `contains(meterName, '…')` and `priceType eq 'Consumption'`.
- Vellum refuses a search value that holds a single quote.
- The response holds `Items` and `NextPageLink`. Vellum reads at most 2 pages and shows at most 500 rows. It follows `NextPageLink` only to the server in the settings.
- Vellum stores `retailPrice`, `unitOfMeasure`, `currencyCode`, `meterId`, `armRegionName`, `skuName`, `productName`, `meterName` and `effectiveStartDate`.
- Vellum does not store a row whose `type` is not `Consumption`, a row with a `reservationTerm`, or a row whose `tierMinimumUnits` is not 0.

The API is public. Vellum sends no credential and no workspace data to it, except the search values. The base URL is the setting `cost.azureRetailPricesBaseUrl`. The instance policy for outbound addresses applies to it.

## Units

A unit is `[number] name [/ period]`. The period is `hour`, `day` or `month`. Examples: `1 Hour`, `GB/month`, `1000000 requests`, `instances`. A space stands between the number and the name. Names are compared in lower case and in the singular.

Vellum converts a quantity to price units with these rules only:

| Quantity | Price | Conversion |
|---|---|---|
| A time, for example `730 hours` | For a time, for example `1 Hour` | A day is 24 hours. A month is 730 hours. |
| `hours/day` | For a time | The hours on each day, times 730 ÷ 24 days. |
| A name with no period, or per month | For the same name, with no period or per month | The quantity is the amount of one month. |
| A name per hour or per day | For the same name | Times 730 hours, or times 730 ÷ 24 days. |
| A name with no period | For the same name per hour or per day | Times 730 hours, or times 730 ÷ 24 days. |
| `instances` | For a time | Each instance runs for 730 hours. |

A month is 730 hours in each estimate: 8760 hours of a year, divided by 12.

All other pairs do not convert. The line is then unknown.

## Estimate

The estimate is a function of the resources, the assumptions, the price entries, the date, the currency of the workspace and the stale age. The same inputs give the same result and the same hash.

- A baseline line is quantity × unit price, after the unit conversion.
- A usage line is assumption × unit price, after the unit conversion.
- All arithmetic is decimal.
- The amount of a line is rounded to 4 decimal places, half away from zero.
- A subtotal or a total is the sum of rounded line amounts. It is not rounded again.
- There is one total for each currency. Vellum converts no currency.
- Each line holds the ID of its price entry and, for a usage line, the ID of its assumption.
- `hash` is the SHA-256 of the canonical JSON of the estimate.

### Unknown reasons

A line with one of these reasons has no amount. It is in no subtotal and in no total.

| Reason | Meaning |
|---|---|
| `no-price` | The line names no price entry. |
| `price-not-found` | The price entry is not in the price book of the workspace. |
| `price-not-effective` | The effective date of the price is after the date of the estimate. |
| `unit-mismatch` | Vellum does not read a unit, or the two units do not convert. |
| `missing-assumption` | The assumption of the usage line is not in the sheet. |
| `assumption-not-effective` | The effective date of the assumption is after the date of the estimate. |

### Flags

A line with a flag has an amount, and the amount is in its total.

| Flag | Meaning |
|---|---|
| `stale-price` | The price was entered or fetched more than `cost.stalePriceAfterDays` days before the date of the estimate. The default is 90. |
| `currency-mismatch` | The price is not in the currency of the workspace. The amount is in the total of its own currency. |

The age of a price counts from the day it was entered or fetched. It does not count from the effective date of the provider, because a provider can keep a price for years.

## Saved estimate

A saved estimate holds the branch, the version of the model stream, the date, the fingerprint of the inputs, the hash and the result. It never changes. When the inputs of the branch change, the estimate is `outdated` and `changes` lists each resource, assumption, price or setting that was added, removed or changed. A branch holds at most 100 saved estimates.

## Comparison

A comparison matches the resources of two branches by element and name. For each total, category and resource it gives the left amount, the right amount and the difference for each currency. The difference is right minus left. A row with an unknown line on either side has no difference.

`comparable` is true only when both sides have no unknown line and all amounts are in one currency. Only then does the comparison give `cheaper` and `difference`. The other cases give one of these reasons: `left-has-unknown-lines`, `right-has-unknown-lines`, `both-sides-have-unknown-lines`, `several-currencies`.

## Endpoints

A project endpoint takes `?branchId=` with the stream ID of a draft or a variant. Without it, the endpoint uses main. A viewer reads. An editor writes. Only an owner changes the price book or searches Azure.

| Method and path | Purpose |
|---|---|
| `GET /api/projects/{projectId}/costs` | The cost sheet. |
| `POST /api/projects/{projectId}/costs/resources` | Add a resource. |
| `PUT`, `DELETE /api/projects/{projectId}/costs/resources/{resourceId}` | Change or remove a resource. |
| `POST /api/projects/{projectId}/costs/assumptions` | Add an assumption. |
| `PUT`, `DELETE /api/projects/{projectId}/costs/assumptions/{assumptionId}` | Change or remove an assumption. |
| `POST /api/projects/{projectId}/costs/copy` | Replace the sheet with a copy of the sheet of the branch in `from`. |
| `GET /api/projects/{projectId}/costs/estimate?asOf=` | The estimate as it is now. Nothing is stored. |
| `GET`, `POST /api/projects/{projectId}/costs/estimates` | List the saved estimates, or save one. |
| `GET`, `DELETE /api/projects/{projectId}/costs/estimates/{estimateId}` | Read or delete a saved estimate. |
| `GET /api/projects/{projectId}/costs/compare?left=&right=&asOf=` | Compare two branches. A side is `main` or a branch ID. |
| `GET /api/projects/{projectId}/costs/prices` | The price book of the workspace of the project, and the pinned entries. |
| `PUT`, `DELETE /api/projects/{projectId}/costs/pins/{priceId}` | Pin or unpin a price entry. |
| `GET`, `POST /api/workspaces/{workspaceId}/prices` | List the price book, or add a custom price. |
| `DELETE /api/workspaces/{workspaceId}/prices/{priceId}` | Delete an entry that no project pins. |
| `GET /api/workspaces/{workspaceId}/prices/azure/meters` | Search Azure meters. Parameters: `serviceName`, `region`, `skuName`, `meterName`, `currency`. |
| `POST /api/workspaces/{workspaceId}/prices/azure` | Store the row with `meterId`, `skuId`, `armRegionName` and `currency`. Vellum reads the row again. |

A workspace makes at most 30 Azure requests in one minute.

When a project is deleted, Vellum deletes its cost sheets, its pins and its saved estimates. The price book stays with the workspace.
