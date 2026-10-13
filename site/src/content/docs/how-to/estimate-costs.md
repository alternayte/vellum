---
title: Estimate costs
description: Add prices, resources and assumptions, read the monthly estimate, save it and compare two variants.
---

This guide shows you how to estimate the monthly infrastructure cost of a model and how to compare two variants.

Vellum ships no price. An estimate uses only the prices in the price book of your workspace. A line without a price is **unknown**. It is never zero.

## Add prices

You need the owner role in the workspace.

1. Open **Settings**, then **Prices**.
2. To enter a price yourself, fill in **Add a custom price** and select **Add price**. Give the price for one unit, for example `0.25` `USD` for one `hour`.
3. To fetch a price from Azure, fill in **Fetch a price from Azure** and select **Search meters**. The service name is necessary, for example `Virtual Machines`. The values are case sensitive.
4. Read the rows. Select **Add to the price book** on the row that you want.

Vellum does not pick a row for you. When you pick a row, Vellum reads that row again from the Azure API. It stores the price, the meter ID and the effective date of that answer.

Vellum fetches prices from Azure only. Enter the prices of AWS, Google Cloud and your own infrastructure as custom prices.

To change a price, add a new entry. An entry never changes.

## Add resources

You need the editor role.

1. Open a project and select **Costs** in the left toolbar.
2. Select **Add** next to **Resources**.
3. Select the element that the resource runs. Give the resource a name, a category and a provider.
4. Enter the quantity for one month and its unit, for example `2` `instances` or `50` `GB`.
5. Select a price entry. Without one, the line is unknown.
6. Select **Save**.

## Add assumptions and usage lines

An assumption is a named number, for example the requests for each month. A usage line multiplies one unit price by one assumption.

1. Select **Add** next to **Assumptions**.
2. Enter the name, the value and the unit, for example `2000000` `requests/month`.
3. Select the label. **declared** is a number that you state. **measured** and **inferred** need a source.
4. Open a resource and select **Add a usage line**. Select the assumption and a price entry.

## Read the estimate

The flyout shows one total for each currency. Vellum converts no currency.

- The total holds known lines only.
- **Unknown** lists each line that has no amount, with the reason.
- Select **Lines, saved estimates and compare** to see each line with its quantity, its price, the unit conversion and the amount.

Each number shows its label. A quantity shows `declared`, `measured` or `inferred`. A price shows `custom` or `fetched` with the date.

## Save an estimate

1. Select **Lines, saved estimates and compare**.
2. Enter a name and select **Save estimate**.

A saved estimate keeps its numbers. When a resource, an assumption, a price or a cost setting changes later, the **Saved** tab shows the estimate as **outdated** and lists what changed.

## Compare two variants

1. In the **Costs** flyout, select **New variant from main** and give the variant a name. The cost sheet of the variant starts as a copy of the sheet that you see.
2. Change the resources of the variant.
3. Select **Lines, saved estimates and compare**, then **Compare**.
4. Select the two branches.

Vellum says which side costs less only when both sides have no unknown line and all amounts are in one currency. In all other cases it shows **Not comparable** and the reason.

A draft has its own cost sheet too. Select **Copy the sheet of main** to start it. When you merge a draft, Vellum does not merge its cost sheet into main.

## Settings

Open **Settings**, then **Cost**.

- **Currency**: the estimate flags a line that is priced in another currency.
- **A price is stale after**: the estimate flags a price that was entered or fetched longer ago than this number of days. The line stays in the total.
- **Azure price API**: the address of the price API. Change it only to use a mirror. An empty value turns the search off.

## What Vellum does not price

- Vellum has no price list of its own.
- Vellum does not fetch prices from AWS or Google Cloud.
- Vellum stores Azure consumption prices only. It does not store reservation prices or savings plan prices.
- Vellum does not model price tiers. It stores the price that applies from zero units.
- Vellum does not convert currencies.
- Vellum does not add discounts, taxes or support plans.
- Vellum does not add the cost of people to a total.

See the [cost model reference](/reference/cost-model/) for the rules.
