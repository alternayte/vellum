---
title: Estimate costs
description: Add prices, resources and assumptions, read the monthly estimate, save it and compare two variants.
---

This guide shows you how to estimate the monthly infrastructure cost of a model and how to compare two variants.

Vellum ships no price. An estimate uses only the prices in the price book of your workspace. A line without a price is **unknown**. It is never zero.

## Add prices

You need the owner role in the workspace. Each member of the workspace can read the price book.

1. Open the workspace on the home screen and select **Settings**, then **Prices**.
2. To enter a price yourself, fill in **Add a custom price** and select **Add price**. Give the price for one unit, for example `0.25` in **Unit price**, `USD` in **Currency** and `hour` in **For one**.
3. To fetch a price from Azure, fill in **Fetch a price from Azure** and select **Search meters**. The service name is necessary, for example `Virtual Machines`. The values are case sensitive.
4. Read the rows. Select **Add to the price book** on the row that you want.

The provider of a custom price is `azure`, `aws`, `gcp` or `custom`. A unit price is a number from 0 to 1000000. A currency is a three-letter code in upper case.

Vellum does not pick a row for you. When you pick a row, Vellum reads that row again from the Azure API. It stores the price, the meter ID and the effective date of that answer.

A search shows 500 rows at most. When the API has more rows, the result says so. Narrow the search. Vellum permits 30 searches and fetches in one minute for each workspace.

Some meters have price tiers. The price then depends on the amount that you use, and the first tier is often free. Vellum shows the rows of such a meter and stores none of them. Enter the price that you pay as a custom price.

A row that Vellum does not store has no **Add to the price book** button. The row shows the reason, for example a row that is not a consumption price.

Vellum fetches prices from Azure only. Enter the prices of AWS, Google Cloud and your own infrastructure as custom prices.

When an entry shows **Vellum does not read this unit**, a line that uses the entry is unknown. Enter the price again as a custom price with a unit that Vellum reads, for example `hour`, `GB/month` or `1000000 requests`.

To change a price, add a new entry. An entry never changes. To remove an entry, select **Delete** on its row. A line that names a deleted entry is unknown.

A price book holds 2000 entries at most.

Vellum records each added, fetched and deleted price in the audit log of the workspace. Open **Settings**, then **Audit log** to read it.

## Add resources

You need the editor role.

1. Open a project and select **Costs** in the left toolbar.
2. Select **Add** next to **Resources**.
3. Select the element that the resource runs on. Give the resource a name, a category and a provider.
4. Under **Baseline**, enter the quantity for one month and its unit, for example `2` `instances` or `50` `GB`.
5. Select a price entry in **Price entry**. Without one, the line is unknown.
6. Select **Save**.

Select a price entry of the same provider as the resource. When the resource and the price entry each name a region, the two regions must be the same. If they are not, the line is unknown.

Enter the quantity that one month holds. For stored data, enter the GB that are stored, for example `50` `GB`. Do not enter the GB that you add on each day.

A quantity in `instances` with a price for one hour runs for 730 hours. Each estimate uses 730 hours as the length of one month.

A unit is a name with an optional period: `/hour`, `/day` or `/month`. A unit can start with a number and a space, for example `1000000 requests`. Vellum does not read a unit such as `10K`.

For a resource that has usage lines only, clear **The resource has a fixed monthly quantity**.

An element cannot have two resources with the same name. A cost sheet holds 500 resources and 500 assumptions at most.

## Add assumptions and usage lines

An assumption is a named number, for example the requests for each month. A usage line multiplies one unit price by one assumption.

1. Select **Add** next to **Assumptions**.
2. Enter the name, the value and the unit, for example `2000000` `requests/month`.
3. Select the label in **Label**. `declared` is a number that you state. `measured` and `inferred` need a source in **Source**.
4. Select **Save**.
5. Open a resource and select **Add a usage line**. Give the line a name. Select the assumption and a price entry.
6. Select **Save**.

A number with no period in its unit is the amount for one month.

A resource has 20 usage lines at most. Two assumptions cannot have the same name.

When you delete an assumption, a usage line that names it stays. The estimate lists that line as unknown.

## Read the estimate

The flyout shows one total for each currency. Vellum converts no currency.

- The total holds known lines only.
- **Unknown** lists each line that has no amount, with the reason.
- Select **Lines, saved estimates and compare** to see each line with its quantity, its price, the unit conversion and the amount.

Each number shows its label. The quantity of a baseline line shows the fact status of the resource. A resource that you add in the app is `declared`. The quantity of a usage line shows the label of its assumption: `declared`, `measured` or `inferred`. A price shows its provider, its region and its SKU, then `custom` or `fetched` with the date.

A line is unknown in these cases:

- **No price**: the line names no price entry, or the resource has no quantity and no usage line.
- **Price entry deleted**: the price entry is no longer in the price book.
- **Price not yet effective**: the effective date of the price is after the date of the estimate.
- **Units do not convert**: Vellum does not read a unit, or the unit of the quantity does not convert to the unit of the price.
- **Assumption missing**: the assumption of the usage line is no longer in the sheet.
- **Assumption not yet effective**: the effective date of the assumption is after the date of the estimate.
- **Price is for another provider** or **Price is for another region**: the price entry does not agree with the resource.
- **Element removed from the model**: the element of the resource is no longer in the model. Remove the resource, or give it to another element.
- **Amount too large**: the amount is too large to compute.

A known line can show a flag. The line stays in the total.

- **stale price**: the price is older than the limit in the settings.
- **other currency**: the price is not in the currency of the workspace.

## Save an estimate

1. Select **Lines, saved estimates and compare**.
2. Enter a name in **Save this estimate as** and select **Save estimate**. Without a name, Vellum uses `Estimate` and the date.

You need the editor role to save an estimate.

A saved estimate keeps its numbers. When a resource, an assumption, a price or a cost setting changes later, the **Saved** tab shows the estimate as **outdated** and lists what changed. An estimate with no change shows **inputs unchanged**.

A branch holds 100 saved estimates at most. Select **Delete** on a saved estimate to remove it.

## Compare two variants

1. In the **Costs** flyout, select **New variant from main**. Give the variant a name and select **Create**. The cost sheet of the variant starts as a copy of the sheet that you see. Vellum makes the variant and the copy together. If one of them fails, Vellum makes neither.
2. Change the resources of the variant.
3. Select **Lines, saved estimates and compare**, then **Compare**.
4. Select the two branches in **Left** and **Right**.

Vellum compares two resources when they have the same element and the same name.

Vellum says which side costs less only when both sides have lines, no line is unknown and all amounts are in one currency. In all other cases it shows **Not comparable** and the reason.

An empty cost sheet is not a cost of zero. When one side has no lines, Vellum shows no difference and does not say that the empty side costs less.

## Use a cost sheet in a draft

A draft has its own cost sheet. The sheet is empty at first. Open the **Costs** flyout while the draft is active, or select the draft in **Branch**. Then select **Copy the sheet of main** to start the sheet. The copy leaves out a resource when its element is not in the model of the draft.

When you merge the draft, Vellum merges its cost sheet into the sheet of main in the same step:

- A resource or an assumption that you changed, added or removed only in the draft changes main in the same way.
- A resource or an assumption that changed only on main stays as it is on main.
- If the draft and main changed the same resource or assumption in different ways, Vellum refuses the merge and shows the reason. Change one of the two sheets until they agree. Then merge again.

When a draft is merged or abandoned, Vellum deletes the cost sheet and the saved estimates of the draft.

## Settings

Open **Settings**, then **Cost**. You need the owner role to change a setting. See [Configure a workspace](/how-to/configure-settings/).

- **Currency**: the estimate flags a line that is priced in another currency.
- **A price is stale after**: the estimate flags a price that was entered or fetched longer ago than this number of days. The line stays in the total. The value is from 1 to 3650 days.
- **Azure price API**: the address of the price API. Change it only to use a mirror. An empty value turns the search off.

## What Vellum does not price

- Vellum has no price list of its own.
- Vellum does not fetch prices from AWS or Google Cloud.
- Vellum stores Azure consumption prices only. It does not store reservation prices.
- Vellum does not model price tiers. It stores no price of a meter that has tiers.
- Vellum does not convert currencies.
- Vellum does not add discounts, taxes or support plans.
- Vellum does not add the cost of people to a total.

## See also

- [Cost model](/reference/cost-model/) for the rules, the units and the endpoints.
- [Estimates are not measurements](/concepts/estimates-are-not-measurements/) for what an estimate means.
- [Compare variants](/how-to/compare-variants/) to compare the models of two variants.
- [Work in a draft](/how-to/work-in-a-draft/) to merge a draft.
