# Orders example

A model of a small shop that you can import into Vellum. It is the Orders reference case:
a customer places an order, the Orders API reserves stock, and `OrderCreated` goes to
Inventory and Billing. It is also the template **Orders** of an empty project, with the
templates in `../url-shortener`, `../chat-service` and `../payments`.

## Files

| File | Content |
|---|---|
| `model.json` | A Vellum model document: 8 elements, 5 relationships, 5 JSON schemas, 3 operations, 4 channels, 2 bindings, 2 subscriptions and 1 message. |
| `scenarios/place-an-order.json` | The flow with no fault. It has 15 events. |
| `scenarios/place-an-order-inventory-down.json` | The same flow with an outage of `POST /reservations`. The failure branch runs: the order waits for stock, and no event is published. It has 13 events. |
| `stress-profile.json` | The stress profile **Busy hour** of `Place an order`: 25 orders each second for two minutes. Every number is declared. |
| `import.sh` | Imports the model and the scenarios into a running Vellum and plays the scenarios. |
| `build.mjs` | Builds the model through the HTTP API and writes the files above, with `../build-kit.mjs`. Maintainers use it. |

## What the model holds

- **Elements:** the actor `Customer`; the system `Shop` with `Orders API`, `Orders DB`,
  `Inventory`, `Inventory DB` and `Billing`; the external system `Payment provider`.
- **Operations:** `POST /orders` (Orders API), `POST /reservations` (Inventory, 3 attempts),
  `POST /charges` (Payment provider, 2 attempts). Each has a request schema.
- **Channels:** the topic `order-events`, the queues `inventory.order-created` and
  `billing.order-created`, and the dead-letter queue `order-events.dlq`.
- **Bindings:** `order-events` to each queue, with the routing keys `order.created` and `order.*`.
- **Subscriptions:** Inventory and Billing, each on its queue, with retries and the dead-letter queue.
- **Message:** `OrderCreated`, with a schema, from the Orders API to Inventory and Billing.

The scenario `Place an order` has a call, a nested call with an error handler, a branch on
the outcome of the reservation, a publish with a routing key, and one handler for each
consumer. Every fact has the truth label `declared`.

## Import it

You need a running Vellum, an account with the editor role, `curl` and `jq`.

```sh
VELLUM_URL=http://localhost:8080 VELLUM_EMAIL=you@example.com VELLUM_PASSWORD='your password' ./import.sh
```

The script prints:

```text
model.json: imported
Place an order when Inventory is down: 13 events, 5 of 5 expectations hold, hash 37fe866d…
Place an order: 15 events, 5 of 5 expectations hold, hash ca0e9eef…
Open http://localhost:8080 and select the project Orders.
```

In the app, open the project **Orders**, select **Scenarios** in the left toolbar and play
a scenario.

The script makes three kinds of call, which you can also make by hand:

1. `POST /api/workspaces/{workspaceId}/projects` creates the project.
2. `POST /api/projects/{projectId}/import` with `model.json` as the body imports the model.
3. `POST /api/projects/{projectId}/scenarios` with each scenario file saves a scenario.
   The model document does not hold scenarios.

## Limits

- **One copy for each instance.** A schema ID and a scenario ID belong to one project of an
  instance. A second import into another project of the same instance is refused. To import
  a second copy, give every ID in the files a new value.
- **The trace hash is stable.** The same model and the same scenario give the same hash on
  every instance. A new version of the trace engine can change it.

## Keep it working

The test `ExampleTests` opens each template as an empty project does: it imports
`model.json` through the real endpoint, compares the export with the file, plays every
scenario and runs the stress profile twice with the same hash. It fails when a change to the
model document format, the scenario format, the trace engine or the stress engine breaks an
example.

To build the files again, start a throwaway instance and run `build.mjs`. The header of
`../build-kit.mjs` gives the command. `site/scripts/verify-live.sh` does this and checks that the files
do not change.
