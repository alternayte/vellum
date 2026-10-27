// Builds the Payments example in a running Vellum through the HTTP API, plays its scenarios,
// runs its stress profile and writes the files of this directory: model.json,
// scenarios/*.json and stress-profile.json. ../build-kit.mjs signs in and writes the files;
// its header gives the command.

import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { finishProject, schema, startProject } from '../build-kit.mjs'

const here = dirname(fileURLToPath(import.meta.url))

// Every ID of the example is fixed, so that the files do not change between builds.
// A parent element has a lower ID than its children: an import adds elements in ID order.
const id = (n) => `e0a1d5c0-15ed-4000-8003-${String(n).padStart(12, '0')}`
const ids = {
  project: id(1),
  payments: id(10),
  merchant: id(11),
  cardNetwork: id(12),
  paymentsApi: id(20),
  paymentsDb: id(21),
  risk: id(22),
  ledger: id(23),
  ledgerDb: id(24),
  webhooks: id(25),
  paymentRequest: id(30),
  paymentResult: id(31),
  riskRequest: id(32),
  authorizationRequest: id(33),
  paymentCapturedSchema: id(34),
  createPayment: id(40),
  checkRisk: id(41),
  authorize: id(42),
  paymentEvents: id(50),
  deadLetters: id(51),
  ledgerSub: id(60),
  webhooksSub: id(61),
  paymentCaptured: id(80),
  relMerchantApi: id(90),
  relApiDb: id(91),
  relApiRisk: id(92),
  relApiNetwork: id(93),
  relApiLedger: id(94),
  relLedgerDb: id(95),
  relApiWebhooks: id(96),
  relWebhooksMerchant: id(97),
  scenarioPay: id(100),
  scenarioTimeout: id(101),
}

const payment = { paymentId: 'p-1', amount: 1999, currency: 'EUR' }

function payScenario(scenarioId, name, faults, assertions) {
  return {
    id: scenarioId,
    name,
    steps: [
      {
        type: 'call',
        id: 'pay',
        from: ids.merchant,
        operation: ids.createPayment,
        request: { amount: 1999, currency: 'EUR', cardToken: 'tok_visa' },
        response: { paymentId: 'p-1', status: 'pending' },
        then: [
          {
            type: 'write',
            id: 'save-payment',
            by: ids.paymentsApi,
            store: ids.paymentsDb,
            dataset: 'payments',
            change: { ...payment, status: 'pending' },
          },
          {
            type: 'call',
            id: 'check-risk',
            from: ids.paymentsApi,
            operation: ids.checkRisk,
            request: payment,
            response: null,
            then: [],
            onError: [],
          },
          {
            type: 'call',
            id: 'authorize',
            from: ids.paymentsApi,
            operation: ids.authorize,
            request: { ...payment, cardToken: 'tok_visa' },
            response: null,
            then: [],
            // The card network gave no answer: the payment waits, and a job asks the network again.
            onError: [
              {
                type: 'write',
                id: 'mark-unknown',
                by: ids.paymentsApi,
                store: ids.paymentsDb,
                dataset: 'payments',
                change: { paymentId: 'p-1', status: 'unknown' },
              },
            ],
          },
          {
            type: 'branch',
            id: 'authorized',
            whenStep: 'authorize',
            is: 'Succeeded',
            then: [
              {
                type: 'write',
                id: 'mark-captured',
                by: ids.paymentsApi,
                store: ids.paymentsDb,
                dataset: 'payments',
                change: { paymentId: 'p-1', status: 'captured' },
              },
              {
                type: 'publish',
                id: 'publish-captured',
                from: ids.paymentsApi,
                channel: ids.paymentEvents,
                message: ids.paymentCaptured,
                payload: payment,
                headers: { 'idempotency-key': 'p-1' },
                handlers: {
                  [ids.ledgerSub]: [
                    {
                      type: 'write',
                      id: 'book-entries',
                      by: ids.ledger,
                      store: ids.ledgerDb,
                      dataset: 'entries',
                      change: { paymentId: 'p-1', debit: 'merchant-receivable', credit: 'card-clearing', amount: 1999 },
                    },
                  ],
                  [ids.webhooksSub]: [],
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
  'take-a-payment': payScenario(
    ids.scenarioPay,
    'Take a payment',
    [],
    [
      { type: 'outcome', id: 'payment-is-accepted', step: 'pay', expected: 'Succeeded' },
      { type: 'outcome', id: 'card-is-authorized', step: 'authorize', expected: 'Succeeded' },
      { type: 'outcome', id: 'payment-is-captured', step: 'mark-captured', expected: 'Succeeded' },
      { type: 'event-count', id: 'captured-is-published-once', kind: 'Published', target: ids.paymentEvents, expected: 1 },
      { type: 'event-count', id: 'ledger-and-webhooks-get-it', kind: 'Delivered', target: null, expected: 2 },
    ],
  ),
  'take-a-payment-card-network-times-out': payScenario(
    ids.scenarioTimeout,
    'Take a payment when the card network times out',
    [{ target: ids.authorize, kind: 'Timeout', failFirst: null }],
    [
      { type: 'outcome', id: 'payment-is-still-accepted', step: 'pay', expected: 'Succeeded' },
      { type: 'outcome', id: 'authorization-fails', step: 'authorize', expected: 'Failed' },
      { type: 'outcome', id: 'payment-waits-as-unknown', step: 'mark-unknown', expected: 'Succeeded' },
      { type: 'event-count', id: 'one-attempt-only', kind: 'RequestTimedOut', target: ids.authorize, expected: 1 },
      { type: 'event-count', id: 'nothing-is-captured', kind: 'Published', target: null, expected: 0 },
    ],
  ),
}

// The stress profile of "Take a payment": a sale day. Every number is declared.
const profile = {
  name: 'Sale day',
  scenarioId: ids.scenarioPay,
  seed: '2026',
  arrivals: { kind: 'poisson', ratePerSecond: 60, durationSeconds: 120, warmUpSeconds: 10 },
  resources: [
    { elementId: ids.cardNetwork, concurrency: 100, queueCapacity: null },
    { elementId: ids.paymentsApi, concurrency: 64, queueCapacity: 500 },
    { elementId: ids.paymentsDb, concurrency: 8, queueCapacity: null },
    { elementId: ids.risk, concurrency: 8, queueCapacity: 500 },
    { elementId: ids.ledgerDb, concurrency: 4, queueCapacity: null },
  ],
  serviceTimes: [
    { target: ids.createPayment, time: { kind: 'lognormal', medianMs: 5, p95Ms: 20 } },
    { target: ids.checkRisk, time: { kind: 'lognormal', medianMs: 40, p95Ms: 120 } },
    { target: ids.authorize, time: { kind: 'lognormal', medianMs: 350, p95Ms: 1200 } },
    { target: ids.paymentsDb, time: { kind: 'exponential', meanMs: 5 } },
    { target: ids.ledgerDb, time: { kind: 'exponential', meanMs: 8 } },
    { target: ids.ledgerSub, time: { kind: 'fixed', ms: 3 } },
    { target: ids.webhooksSub, time: { kind: 'uniform', minMs: 20, maxMs: 200 } },
  ],
  subscriptions: [
    { subscriptionId: ids.ledgerSub, consumers: 2, queueCapacity: null },
    { subscriptionId: ids.webhooksSub, consumers: 16, queueCapacity: 10000 },
  ],
  branches: [{ stepId: 'authorized', probability: 0.92 }],
  overrides: [],
  outages: [],
}

// --- project -------------------------------------------------------------------------------

const { post } = await startProject({
  id: ids.project,
  name: 'Payments',
  description: 'Card payments: the Payments API checks risk, asks the card network to authorise, and books the capture in the Ledger.',
})

// --- schemas -------------------------------------------------------------------------------

const text = { type: 'string', minLength: 1 }
const ident = { type: 'string', pattern: '^p-[0-9]+$' }
const amount = { type: 'integer', minimum: 1 }
const currency = { type: 'string', pattern: '^[A-Z]{3}$' }
await post('schemas', { id: ids.paymentRequest, name: 'PaymentRequest', description: 'The body of POST /payments. The amount is in minor units.', content: schema({ amount, currency, cardToken: text }, ['amount', 'currency', 'cardToken']) })
await post('schemas', { id: ids.paymentResult, name: 'PaymentResult', description: 'The answer of POST /payments.', content: schema({ paymentId: ident, status: { enum: ['pending', 'captured', 'declined'] } }, ['paymentId', 'status']) })
await post('schemas', { id: ids.riskRequest, name: 'RiskRequest', description: 'The body of POST /risk-checks.', content: schema({ paymentId: ident, amount, currency }, ['paymentId', 'amount', 'currency']) })
await post('schemas', { id: ids.authorizationRequest, name: 'AuthorizationRequest', description: 'The body of POST /authorizations.', content: schema({ paymentId: ident, amount, currency, cardToken: text }, ['paymentId', 'amount', 'currency', 'cardToken']) })
await post('schemas', { id: ids.paymentCapturedSchema, name: 'PaymentCaptured', description: 'A payment is authorised and captured.', content: schema({ paymentId: ident, amount, currency }, ['paymentId', 'amount', 'currency']) })

// --- elements and relationships ------------------------------------------------------------

const element = (elementId, kind, name, more = {}) => post('elements', { id: elementId, kind, name, tags: [], ...more })
await element(ids.payments, 'system', 'Payments', { description: 'Takes card payments for merchants and books them.' })
await element(ids.merchant, 'actor', 'Merchant', { description: 'A shop that takes payments through the API.' })
await element(ids.cardNetwork, 'system', 'Card network', { description: 'An external network that authorises a card payment, such as Visa or Mastercard.', tags: ['external'] })
await element(ids.paymentsApi, 'app', 'Payments API', { description: 'Takes a payment, checks its risk and asks the card network to authorise it.', technology: 'Java', parentId: ids.payments })
await element(ids.paymentsDb, 'store', 'Payments DB', { description: 'Holds each payment and its status.', technology: 'PostgreSQL', parentId: ids.payments })
await element(ids.risk, 'app', 'Risk engine', { description: 'Scores a payment for fraud.', technology: 'Python', parentId: ids.payments })
await element(ids.ledger, 'app', 'Ledger', { description: 'Books a double entry for each captured payment.', technology: 'Java', parentId: ids.payments })
await element(ids.ledgerDb, 'store', 'Ledger DB', { description: 'Holds the entries. An entry is never changed.', technology: 'PostgreSQL', parentId: ids.payments })
await element(ids.webhooks, 'app', 'Webhooks', { description: 'Tells the merchant that a payment is captured.', technology: 'Go', parentId: ids.payments })

const relationship = (relationshipId, fromId, toId, label, technology) => post('relationships', { id: relationshipId, fromId, toId, label, technology })
await relationship(ids.relMerchantApi, ids.merchant, ids.paymentsApi, 'takes payments', 'HTTPS')
await relationship(ids.relApiDb, ids.paymentsApi, ids.paymentsDb, 'reads and writes', 'SQL')
await relationship(ids.relApiRisk, ids.paymentsApi, ids.risk, 'checks risk', 'HTTP')
await relationship(ids.relApiNetwork, ids.paymentsApi, ids.cardNetwork, 'authorises cards', 'ISO 8583')
await relationship(ids.relApiLedger, ids.paymentsApi, ids.ledger, 'sends PaymentCaptured', 'Kafka')
await relationship(ids.relLedgerDb, ids.ledger, ids.ledgerDb, 'appends entries', 'SQL')
await relationship(ids.relApiWebhooks, ids.paymentsApi, ids.webhooks, 'sends PaymentCaptured', 'Kafka')
await relationship(ids.relWebhooksMerchant, ids.webhooks, ids.merchant, 'notifies', 'HTTPS')

// --- contracts -----------------------------------------------------------------------------

await post('operations', { id: ids.createPayment, ownerId: ids.paymentsApi, name: 'POST /payments', transport: 'http', requestSchemaId: ids.paymentRequest, responseSchemaId: ids.paymentResult, timeoutMs: 10000 })
await post('operations', { id: ids.checkRisk, ownerId: ids.risk, name: 'POST /risk-checks', transport: 'http', requestSchemaId: ids.riskRequest, timeoutMs: 300, retry: { maxAttempts: 2, backoffMs: 50 } })
// One attempt: a second authorisation could charge the card twice.
await post('operations', { id: ids.authorize, ownerId: ids.cardNetwork, name: 'POST /authorizations', transport: 'http', requestSchemaId: ids.authorizationRequest, timeoutMs: 5000 })

await post('channels', { id: ids.paymentEvents, name: 'payment-events', kind: 'topic' })
await post('channels', { id: ids.deadLetters, name: 'payment-events.dlq', kind: 'queue' })

await post('subscriptions', { id: ids.ledgerSub, channelId: ids.paymentEvents, consumerId: ids.ledger, retry: { maxAttempts: 5, backoffMs: 200 }, deadLetterChannelId: ids.deadLetters })
await post('subscriptions', { id: ids.webhooksSub, channelId: ids.paymentEvents, consumerId: ids.webhooks, retry: { maxAttempts: 8, backoffMs: 1000 }, deadLetterChannelId: ids.deadLetters })

await post('messages', { id: ids.paymentCaptured, name: 'PaymentCaptured', description: 'A payment is authorised and captured.', producerId: ids.paymentsApi, consumerIds: [ids.ledger, ids.webhooks], schemaId: ids.paymentCapturedSchema, tags: [] })

// --- scenarios, the stress profile and the files ------------------------------------------

await finishProject(here, `/api/projects/${ids.project}`, scenarios, profile)
