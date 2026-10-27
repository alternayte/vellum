// Builds the Chat service example in a running Vellum through the HTTP API, plays its
// scenarios, runs its stress profile and writes the files of this directory: model.json,
// scenarios/*.json and stress-profile.json. ../build-kit.mjs signs in and writes the files;
// its header gives the command.

import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { finishProject, schema, startProject } from '../build-kit.mjs'

const here = dirname(fileURLToPath(import.meta.url))

// Every ID of the example is fixed, so that the files do not change between builds.
// A parent element has a lower ID than its children: an import adds elements in ID order.
const id = (n) => `e0a1d5c0-15ed-4000-8002-${String(n).padStart(12, '0')}`
const ids = {
  project: id(1),
  chat: id(10),
  member: id(11),
  pushService: id(12),
  gateway: id(20),
  messagesService: id(21),
  messagesDb: id(22),
  presence: id(23),
  notifier: id(24),
  sendMessage: id(30),
  messageAccepted: id(31),
  storeMessage: id(32),
  messageSentSchema: id(33),
  pushRequest: id(34),
  send: id(40),
  store: id(41),
  push: id(42),
  messageEvents: id(50),
  notifierQueue: id(51),
  deadLetters: id(52),
  notifierSub: id(60),
  notifierBinding: id(70),
  messageSent: id(80),
  relMemberGateway: id(90),
  relGatewayMessages: id(91),
  relMessagesDb: id(92),
  relGatewayPresence: id(93),
  relMessagesNotifier: id(94),
  relNotifierPresence: id(95),
  relNotifierPush: id(96),
  scenarioSend: id(100),
  scenarioPushDown: id(101),
}

const message = { conversationId: 'c-42', messageId: 'm-1', senderId: 'u-7', text: 'Lunch at noon?' }

function sendScenario(scenarioId, name, faults, assertions) {
  return {
    id: scenarioId,
    name,
    steps: [
      {
        type: 'call',
        id: 'send',
        from: ids.member,
        operation: ids.send,
        request: { conversationId: 'c-42', text: 'Lunch at noon?' },
        response: { messageId: 'm-1', status: 'accepted' },
        then: [
          {
            type: 'call',
            id: 'store',
            from: ids.gateway,
            operation: ids.store,
            request: message,
            response: null,
            then: [
              {
                type: 'write',
                id: 'save-message',
                by: ids.messagesService,
                store: ids.messagesDb,
                dataset: 'messages',
                change: message,
              },
              {
                type: 'publish',
                id: 'publish-message-sent',
                from: ids.messagesService,
                channel: ids.messageEvents,
                message: ids.messageSent,
                routingKey: 'message.sent',
                payload: { conversationId: 'c-42', messageId: 'm-1', recipientIds: ['u-8', 'u-9'] },
                headers: {},
                handlers: {
                  [ids.notifierSub]: [
                    {
                      type: 'call',
                      id: 'push',
                      from: ids.notifier,
                      operation: ids.push,
                      request: { deviceTokens: ['d-8', 'd-9'], title: 'New message', body: 'Lunch at noon?' },
                      response: null,
                      then: [],
                      onError: [],
                    },
                  ],
                },
              },
            ],
            onError: [],
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
  'send-a-message': sendScenario(
    ids.scenarioSend,
    'Send a message',
    [],
    [
      { type: 'outcome', id: 'message-is-accepted', step: 'send', expected: 'Succeeded' },
      { type: 'outcome', id: 'message-is-stored', step: 'save-message', expected: 'Succeeded' },
      { type: 'event-count', id: 'one-event-is-published', kind: 'Published', target: ids.messageEvents, expected: 1 },
      { type: 'event-count', id: 'the-notifier-gets-it', kind: 'Delivered', target: null, expected: 1 },
      { type: 'event-count', id: 'nothing-goes-to-dead-letters', kind: 'DeadLettered', target: null, expected: 0 },
    ],
  ),
  'send-a-message-push-service-down': sendScenario(
    ids.scenarioPushDown,
    'Send a message when the push service is down',
    [{ target: ids.push, kind: 'Outage', failFirst: null }],
    [
      { type: 'outcome', id: 'message-is-still-accepted', step: 'send', expected: 'Succeeded' },
      { type: 'outcome', id: 'message-is-still-stored', step: 'save-message', expected: 'Succeeded' },
      { type: 'event-count', id: 'the-push-goes-to-dead-letters', kind: 'DeadLettered', target: null, expected: 1 },
    ],
  ),
}

// The stress profile of "Send a message": the evening peak. Every number is declared.
const profile = {
  name: 'Evening peak',
  scenarioId: ids.scenarioSend,
  seed: '11',
  arrivals: { kind: 'burst', ratePerSecond: 120, bursts: [{ fromSeconds: 60, toSeconds: 75, ratePerSecond: 600 }], durationSeconds: 120, warmUpSeconds: 10 },
  resources: [
    { elementId: ids.pushService, concurrency: 64, queueCapacity: null },
    { elementId: ids.gateway, concurrency: 32, queueCapacity: 2000 },
    { elementId: ids.messagesService, concurrency: 16, queueCapacity: 1000 },
    { elementId: ids.messagesDb, concurrency: 12, queueCapacity: null },
  ],
  serviceTimes: [
    { target: ids.send, time: { kind: 'lognormal', medianMs: 2, p95Ms: 8 } },
    { target: ids.store, time: { kind: 'lognormal', medianMs: 3, p95Ms: 10 } },
    { target: ids.push, time: { kind: 'lognormal', medianMs: 120, p95Ms: 400 } },
    { target: ids.messagesDb, time: { kind: 'lognormal', medianMs: 6, p95Ms: 25 } },
    { target: ids.notifierSub, time: { kind: 'fixed', ms: 2 } },
  ],
  subscriptions: [{ subscriptionId: ids.notifierSub, consumers: 32, queueCapacity: 20000 }],
  branches: [],
  overrides: [],
  outages: [],
}

// --- project -------------------------------------------------------------------------------

const { post } = await startProject({
  id: ids.project,
  name: 'Chat service',
  description: 'Group chat: the gateway takes a message, the Messages service stores it, and the Notifier pushes it to phones.',
})

// --- schemas -------------------------------------------------------------------------------

const text = { type: 'string', minLength: 1 }
const ident = { type: 'string', pattern: '^[a-z]-[0-9]+$' }
await post('schemas', { id: ids.sendMessage, name: 'SendMessage', description: 'The body of POST /messages.', content: schema({ conversationId: ident, text }, ['conversationId', 'text']) })
await post('schemas', { id: ids.messageAccepted, name: 'MessageAccepted', description: 'The answer of POST /messages.', content: schema({ messageId: ident, status: { enum: ['accepted'] } }, ['messageId', 'status']) })
await post('schemas', { id: ids.storeMessage, name: 'StoreMessage', description: 'The request of StoreMessage.', content: schema({ conversationId: ident, messageId: ident, senderId: ident, text }, ['conversationId', 'messageId', 'senderId', 'text']) })
await post('schemas', { id: ids.messageSentSchema, name: 'MessageSent', description: 'A message is stored in its conversation.', content: schema({ conversationId: ident, messageId: ident, recipientIds: { type: 'array', items: ident, minItems: 1 } }, ['conversationId', 'messageId', 'recipientIds']) })
await post('schemas', { id: ids.pushRequest, name: 'PushRequest', description: 'The body of POST /push.', content: schema({ deviceTokens: { type: 'array', items: text, minItems: 1 }, title: text, body: text }, ['deviceTokens', 'title', 'body']) })

// --- elements and relationships ------------------------------------------------------------

const element = (elementId, kind, name, more = {}) => post('elements', { id: elementId, kind, name, tags: [], ...more })
await element(ids.chat, 'system', 'Chat', { description: 'Carries messages between the members of a conversation.' })
await element(ids.member, 'actor', 'Member', { description: 'A person in a conversation.' })
await element(ids.pushService, 'system', 'Push service', { description: 'An external service that delivers a notification to a phone, such as APNs or FCM.', tags: ['external'] })
await element(ids.gateway, 'app', 'Chat gateway', { description: 'Holds the WebSocket of each member and takes the messages they send.', technology: 'Node.js', parentId: ids.chat })
await element(ids.messagesService, 'app', 'Messages service', { description: 'Stores each message and publishes MessageSent.', technology: 'Go', parentId: ids.chat })
await element(ids.messagesDb, 'store', 'Messages DB', { description: 'Holds the messages of each conversation.', technology: 'Cassandra', parentId: ids.chat })
await element(ids.presence, 'store', 'Presence cache', { description: 'Holds which members are online and on which device.', technology: 'Redis', parentId: ids.chat })
await element(ids.notifier, 'app', 'Notifier', { description: 'Sends a push notification to each member who is offline.', technology: 'Go', parentId: ids.chat })

const relationship = (relationshipId, fromId, toId, label, technology) => post('relationships', { id: relationshipId, fromId, toId, label, technology })
await relationship(ids.relMemberGateway, ids.member, ids.gateway, 'sends and reads messages', 'WebSocket')
await relationship(ids.relGatewayMessages, ids.gateway, ids.messagesService, 'stores messages', 'gRPC')
await relationship(ids.relMessagesDb, ids.messagesService, ids.messagesDb, 'reads and writes', 'CQL')
await relationship(ids.relGatewayPresence, ids.gateway, ids.presence, 'records who is online', 'RESP')
await relationship(ids.relMessagesNotifier, ids.messagesService, ids.notifier, 'sends MessageSent', 'AMQP')
await relationship(ids.relNotifierPresence, ids.notifier, ids.presence, 'reads who is online', 'RESP')
await relationship(ids.relNotifierPush, ids.notifier, ids.pushService, 'sends notifications', 'HTTPS')

// --- contracts -----------------------------------------------------------------------------

await post('operations', { id: ids.send, ownerId: ids.gateway, name: 'POST /messages', transport: 'http', requestSchemaId: ids.sendMessage, responseSchemaId: ids.messageAccepted, timeoutMs: 3000 })
await post('operations', { id: ids.store, ownerId: ids.messagesService, name: 'StoreMessage', transport: 'grpc', requestSchemaId: ids.storeMessage, timeoutMs: 1000, retry: { maxAttempts: 2, backoffMs: 50 } })
await post('operations', { id: ids.push, ownerId: ids.pushService, name: 'POST /push', transport: 'http', requestSchemaId: ids.pushRequest, timeoutMs: 2000, retry: { maxAttempts: 2, backoffMs: 500 } })

await post('channels', { id: ids.messageEvents, name: 'message-events', kind: 'topic' })
await post('channels', { id: ids.notifierQueue, name: 'notifier.message-sent', kind: 'queue' })
await post('channels', { id: ids.deadLetters, name: 'message-events.dlq', kind: 'queue' })

await post('bindings', { id: ids.notifierBinding, sourceChannelId: ids.messageEvents, targetChannelId: ids.notifierQueue, routingKey: 'message.sent' })

await post('subscriptions', { id: ids.notifierSub, channelId: ids.notifierQueue, consumerId: ids.notifier, retry: { maxAttempts: 3, backoffMs: 1000 }, deadLetterChannelId: ids.deadLetters })

await post('messages', { id: ids.messageSent, name: 'MessageSent', description: 'A message is stored and its recipients can be told.', producerId: ids.messagesService, consumerIds: [ids.notifier], schemaId: ids.messageSentSchema, tags: [] })

// --- scenarios, the stress profile and the files ------------------------------------------

await finishProject(here, `/api/projects/${ids.project}`, scenarios, profile)
