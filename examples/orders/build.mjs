// Builds the Orders example in a running Vellum through the HTTP API, plays its scenarios, runs
// its stress profile and writes the files of this directory: model.json, scenarios/*.json and
// stress-profile.json. ../build-kit.mjs signs in and writes the files.
//
//   VELLUM_URL=http://localhost:8080 VELLUM_EMAIL=you@example.com VELLUM_PASSWORD=... node build.mjs
//
// On an instance with no account, give VELLUM_SETUP_CODE in place of an existing account:
// the script then completes first-run setup with VELLUM_EMAIL and VELLUM_PASSWORD.
//
// Use a throwaway instance. A schema ID and a scenario ID belong to one project of an
// instance, so the script fails on an instance that already holds this example.

import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { finishProject, schema, startProject } from '../build-kit.mjs'

const here = dirname(fileURLToPath(import.meta.url))

// Every ID of the example is fixed, so that the files do not change between builds.
// A parent element has a lower ID than its children: an import adds elements in ID order.
const id = (n) => `e0a1d5c0-15ed-4000-8000-${String(n).padStart(12, '0')}`
const ids = {
  project: id(1),
  shop: id(10),
  customer: id(11),
  paymentProvider: id(12),
  ordersApi: id(20),
  ordersDb: id(21),
  inventory: id(22),
  inventoryDb: id(23),
  billing: id(24),
  orderRequest: id(30),
  orderAccepted: id(31),
  orderCreated: id(32),
  reservationRequest: id(33),
  chargeRequest: id(34),
  placeOrder: id(40),
  reserveStock: id(41),
  charge: id(42),
  orderEvents: id(50),
  inventoryQueue: id(51),
  billingQueue: id(52),
  deadLetters: id(53),
  inventorySub: id(60),
  billingSub: id(61),
  inventoryBinding: id(70),
  billingBinding: id(71),
  orderCreatedMessage: id(80),
  relCustomerOrders: id(90),
  relOrdersDb: id(91),
  relOrdersInventory: id(92),
  relInventoryDb: id(93),
  relBillingProvider: id(94),
  scenario: id(100),
  scenarioInventoryDown: id(101),
}

function scenario(scenarioId, name, faults, assertions) {
  return {
    id: scenarioId,
    name,
    steps: [
      {
        type: 'call',
        id: 'place',
        from: ids.customer,
        operation: ids.placeOrder,
        request: { sku: 'apple', qty: 2 },
        response: { orderId: 'o-1', status: 'accepted' },
        then: [
          {
            type: 'write',
            id: 'save-order',
            by: ids.ordersApi,
            store: ids.ordersDb,
            dataset: 'orders',
            change: { orderId: 'o-1', status: 'accepted' },
          },
          {
            type: 'call',
            id: 'reserve',
            from: ids.ordersApi,
            operation: ids.reserveStock,
            request: { orderId: 'o-1', sku: 'apple', qty: 2 },
            response: null,
            then: [
              {
                type: 'write',
                id: 'save-reservation',
                by: ids.inventory,
                store: ids.inventoryDb,
                dataset: 'reservations',
                change: { orderId: 'o-1', sku: 'apple', qty: 2 },
              },
            ],
            // The failure is handled here: the order stays accepted and waits for stock.
            onError: [
              {
                type: 'write',
                id: 'mark-waiting-for-stock',
                by: ids.ordersApi,
                store: ids.ordersDb,
                dataset: 'orders',
                change: { orderId: 'o-1', status: 'waiting-for-stock' },
              },
            ],
          },
          {
            type: 'branch',
            id: 'stock-reserved',
            whenStep: 'reserve',
            is: 'Succeeded',
            then: [
              {
                type: 'publish',
                id: 'publish-order-created',
                from: ids.ordersApi,
                channel: ids.orderEvents,
                message: ids.orderCreatedMessage,
                routingKey: 'order.created',
                payload: { orderId: 'o-1', sku: 'apple', qty: 2 },
                headers: { 'x-correlation-id': 'c-1' },
                handlers: {
                  [ids.inventorySub]: [
                    {
                      type: 'write',
                      id: 'confirm-reservation',
                      by: ids.inventory,
                      store: ids.inventoryDb,
                      dataset: 'reservations',
                      change: { orderId: 'o-1', state: 'confirmed' },
                    },
                  ],
                  [ids.billingSub]: [
                    {
                      type: 'call',
                      id: 'charge',
                      from: ids.billing,
                      operation: ids.charge,
                      request: { orderId: 'o-1', amount: 4.2, currency: 'EUR' },
                      response: null,
                      then: [],
                      onError: [],
                    },
                  ],
                },
              },
            ],
            else: [],
          },
        ],
        onError: [],
      },
    ],
    faults,
    assertions,
  }
}

const scenarios = {
  'place-an-order': scenario(
    ids.scenario,
    'Place an order',
    [],
    [
      { type: 'outcome', id: 'order-accepted', step: 'place', expected: 'Succeeded' },
      { type: 'outcome', id: 'stock-is-reserved', step: 'reserve', expected: 'Succeeded' },
      { type: 'event-count', id: 'order-created-published-once', kind: 'Published', target: ids.orderEvents, expected: 1 },
      { type: 'event-count', id: 'both-consumers-get-the-event', kind: 'Delivered', target: null, expected: 2 },
      { type: 'event-count', id: 'nothing-is-lost', kind: 'Dropped', target: null, expected: 0 },
    ],
  ),
  'place-an-order-inventory-down': scenario(
    ids.scenarioInventoryDown,
    'Place an order when Inventory is down',
    [{ target: ids.reserveStock, kind: 'Outage', failFirst: null }],
    [
      { type: 'outcome', id: 'order-still-accepted', step: 'place', expected: 'Succeeded' },
      { type: 'outcome', id: 'reservation-fails', step: 'reserve', expected: 'Failed' },
      { type: 'outcome', id: 'order-waits-for-stock', step: 'mark-waiting-for-stock', expected: 'Succeeded' },
      { type: 'event-count', id: 'reservation-is-tried-three-times', kind: 'RequestFailed', target: ids.reserveStock, expected: 3 },
      { type: 'event-count', id: 'no-order-created-without-stock', kind: 'Published', target: null, expected: 0 },
    ],
  ),
}

// The stress profile of "Place an order": a busy hour of a small shop. Every number is declared.
const profile = {
  name: 'Busy hour',
  scenarioId: ids.scenario,
  seed: '42',
  arrivals: { kind: 'poisson', ratePerSecond: 25, durationSeconds: 120, warmUpSeconds: 10 },
  resources: [
    { elementId: ids.paymentProvider, concurrency: 16, queueCapacity: null },
    { elementId: ids.ordersApi, concurrency: 8, queueCapacity: 200 },
    { elementId: ids.ordersDb, concurrency: 4, queueCapacity: null },
    { elementId: ids.inventory, concurrency: 4, queueCapacity: 200 },
    { elementId: ids.inventoryDb, concurrency: 4, queueCapacity: null },
  ],
  serviceTimes: [
    { target: ids.placeOrder, time: { kind: 'lognormal', medianMs: 15, p95Ms: 60 } },
    { target: ids.reserveStock, time: { kind: 'lognormal', medianMs: 10, p95Ms: 40 } },
    { target: ids.charge, time: { kind: 'lognormal', medianMs: 180, p95Ms: 600 } },
    { target: ids.ordersDb, time: { kind: 'exponential', meanMs: 4 } },
    { target: ids.inventoryDb, time: { kind: 'exponential', meanMs: 4 } },
    { target: ids.inventorySub, time: { kind: 'uniform', minMs: 5, maxMs: 20 } },
    { target: ids.billingSub, time: { kind: 'fixed', ms: 20 } },
  ],
  subscriptions: [
    { subscriptionId: ids.inventorySub, consumers: 2, queueCapacity: null },
    { subscriptionId: ids.billingSub, consumers: 8, queueCapacity: 1000 },
  ],
  branches: [{ stepId: 'stock-reserved', probability: 0.97 }],
  overrides: [],
  outages: [],
}

// --- project -------------------------------------------------------------------------------

const { post } = await startProject({
  id: ids.project,
  name: 'Orders',
  description: 'The Orders reference case: an order is placed, stock is reserved, and OrderCreated goes to Inventory and Billing.',
})

// --- schemas -------------------------------------------------------------------------------

const text = { type: 'string', minLength: 1 }
const quantity = { type: 'integer', minimum: 1 }
await post('schemas', { id: ids.orderRequest, name: 'OrderRequest', description: 'The body of POST /orders.', content: schema({ sku: text, qty: quantity }, ['sku', 'qty']) })
await post('schemas', { id: ids.orderAccepted, name: 'OrderAccepted', description: 'The answer of POST /orders.', content: schema({ orderId: text, status: { enum: ['accepted'] } }, ['orderId', 'status']) })
await post('schemas', { id: ids.orderCreated, name: 'OrderCreated', description: 'The event that the Orders API publishes for an accepted order with reserved stock.', content: schema({ orderId: text, sku: text, qty: quantity }, ['orderId', 'sku', 'qty']) })
await post('schemas', { id: ids.reservationRequest, name: 'ReservationRequest', description: 'The body of POST /reservations.', content: schema({ orderId: text, sku: text, qty: quantity }, ['orderId', 'sku', 'qty']) })
await post('schemas', { id: ids.chargeRequest, name: 'ChargeRequest', description: 'The body of POST /charges.', content: schema({ orderId: text, amount: { type: 'number', exclusiveMinimum: 0 }, currency: { type: 'string', pattern: '^[A-Z]{3}$' } }, ['orderId', 'amount', 'currency']) })

// --- elements and relationships ------------------------------------------------------------

const element = (elementId, kind, name, more = {}) => post('elements', { id: elementId, kind, name, tags: [], ...more })
await element(ids.shop, 'system', 'Shop', { description: 'Takes orders and fulfils them.' })
await element(ids.customer, 'actor', 'Customer', { description: 'A person who places an order.' })
await element(ids.paymentProvider, 'system', 'Payment provider', { description: 'An external service that charges a card.', tags: ['external'] })
await element(ids.ordersApi, 'app', 'Orders API', { description: 'Accepts orders, stores them and publishes OrderCreated.', technology: 'ASP.NET Core', parentId: ids.shop })
await element(ids.ordersDb, 'store', 'Orders DB', { description: 'Holds the orders.', technology: 'PostgreSQL', parentId: ids.shop })
await element(ids.inventory, 'app', 'Inventory', { description: 'Reserves stock for an order.', technology: 'Go', parentId: ids.shop })
await element(ids.inventoryDb, 'store', 'Inventory DB', { description: 'Holds stock levels and reservations.', technology: 'PostgreSQL', parentId: ids.shop })
await element(ids.billing, 'app', 'Billing', { description: 'Charges the customer for an order.', technology: 'ASP.NET Core', parentId: ids.shop })

const relationship = (relationshipId, fromId, toId, label, technology) => post('relationships', { id: relationshipId, fromId, toId, label, technology })
await relationship(ids.relCustomerOrders, ids.customer, ids.ordersApi, 'places orders', 'HTTPS')
await relationship(ids.relOrdersDb, ids.ordersApi, ids.ordersDb, 'reads and writes', 'SQL')
await relationship(ids.relOrdersInventory, ids.ordersApi, ids.inventory, 'reserves stock', 'HTTP')
await relationship(ids.relInventoryDb, ids.inventory, ids.inventoryDb, 'reads and writes', 'SQL')
await relationship(ids.relBillingProvider, ids.billing, ids.paymentProvider, 'charges cards', 'HTTPS')

// --- contracts -----------------------------------------------------------------------------

await post('operations', { id: ids.placeOrder, ownerId: ids.ordersApi, name: 'POST /orders', transport: 'http', requestSchemaId: ids.orderRequest, responseSchemaId: ids.orderAccepted, timeoutMs: 5000 })
await post('operations', { id: ids.reserveStock, ownerId: ids.inventory, name: 'POST /reservations', transport: 'http', requestSchemaId: ids.reservationRequest, timeoutMs: 500, retry: { maxAttempts: 3, backoffMs: 100 } })
await post('operations', { id: ids.charge, ownerId: ids.paymentProvider, name: 'POST /charges', transport: 'http', requestSchemaId: ids.chargeRequest, timeoutMs: 2000, retry: { maxAttempts: 2, backoffMs: 500 } })

await post('channels', { id: ids.orderEvents, name: 'order-events', kind: 'topic' })
await post('channels', { id: ids.inventoryQueue, name: 'inventory.order-created', kind: 'queue' })
await post('channels', { id: ids.billingQueue, name: 'billing.order-created', kind: 'queue' })
await post('channels', { id: ids.deadLetters, name: 'order-events.dlq', kind: 'queue' })

await post('bindings', { id: ids.inventoryBinding, sourceChannelId: ids.orderEvents, targetChannelId: ids.inventoryQueue, routingKey: 'order.created' })
await post('bindings', { id: ids.billingBinding, sourceChannelId: ids.orderEvents, targetChannelId: ids.billingQueue, routingKey: 'order.*' })

await post('subscriptions', { id: ids.inventorySub, channelId: ids.inventoryQueue, consumerId: ids.inventory, retry: { maxAttempts: 3, backoffMs: 200 }, deadLetterChannelId: ids.deadLetters })
await post('subscriptions', { id: ids.billingSub, channelId: ids.billingQueue, consumerId: ids.billing, retry: { maxAttempts: 2, backoffMs: 1000 }, deadLetterChannelId: ids.deadLetters })

await post('messages', { id: ids.orderCreatedMessage, name: 'OrderCreated', description: 'An order is accepted and its stock is reserved.', producerId: ids.ordersApi, consumerIds: [ids.inventory, ids.billing], schemaId: ids.orderCreated, tags: [] })

// --- scenarios, the stress profile and the files ------------------------------------------

await finishProject(here, `/api/projects/${ids.project}`, scenarios, profile)
