// Builds the URL shortener example in a running Vellum through the HTTP API, plays its
// scenarios, runs its stress profile and writes the files of this directory: model.json,
// scenarios/*.json and stress-profile.json. ../build-kit.mjs signs in and writes the files;
// its header gives the command.

import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { finishProject, schema, startProject } from '../build-kit.mjs'

const here = dirname(fileURLToPath(import.meta.url))

// Every ID of the example is fixed, so that the files do not change between builds.
// A parent element has a lower ID than its children: an import adds elements in ID order.
const id = (n) => `e0a1d5c0-15ed-4000-8001-${String(n).padStart(12, '0')}`
const ids = {
  project: id(1),
  shortener: id(10),
  creator: id(11),
  visitor: id(12),
  linksApi: id(20),
  linksDb: id(21),
  linkCache: id(22),
  analytics: id(23),
  clicksDb: id(24),
  createLink: id(30),
  linkCreated: id(31),
  redirectRequest: id(32),
  redirect: id(33),
  linkClickedSchema: id(34),
  shorten: id(40),
  follow: id(41),
  clicks: id(50),
  clicksDeadLetters: id(51),
  analyticsSub: id(60),
  linkClicked: id(80),
  relCreatorApi: id(90),
  relVisitorApi: id(91),
  relApiDb: id(92),
  relApiCache: id(93),
  relApiAnalytics: id(94),
  relAnalyticsClicks: id(95),
  scenarioFollow: id(100),
  scenarioShorten: id(101),
}

const scenarios = {
  'follow-a-short-link': {
    id: ids.scenarioFollow,
    name: 'Follow a short link',
    steps: [
      {
        type: 'call',
        id: 'follow',
        from: ids.visitor,
        operation: ids.follow,
        request: { code: 'spring' },
        response: { location: 'https://example.com/spring-sale' },
        then: [
          {
            type: 'write',
            id: 'touch-link',
            by: ids.linksApi,
            store: ids.linksDb,
            dataset: 'links',
            change: { code: 'spring', lastVisitedAt: '2026-04-01T09:00:00Z' },
          },
          {
            type: 'publish',
            id: 'publish-click',
            from: ids.linksApi,
            channel: ids.clicks,
            message: ids.linkClicked,
            payload: { code: 'spring', at: '2026-04-01T09:00:00Z' },
            headers: {},
            handlers: {
              [ids.analyticsSub]: [
                {
                  type: 'write',
                  id: 'count-click',
                  by: ids.analytics,
                  store: ids.clicksDb,
                  dataset: 'clicks',
                  change: { code: 'spring', at: '2026-04-01T09:00:00Z' },
                },
              ],
            },
          },
        ],
        onError: [],
      },
    ],
    faults: [],
    assertions: [
      { type: 'outcome', id: 'visitor-is-redirected', step: 'follow', expected: 'Succeeded' },
      { type: 'event-count', id: 'one-click-is-published', kind: 'Published', target: ids.clicks, expected: 1 },
      { type: 'event-count', id: 'analytics-gets-the-click', kind: 'Delivered', target: null, expected: 1 },
      { type: 'event-count', id: 'nothing-is-lost', kind: 'Dropped', target: null, expected: 0 },
    ],
  },
  'shorten-a-link': {
    id: ids.scenarioShorten,
    name: 'Shorten a link',
    steps: [
      {
        type: 'call',
        id: 'shorten',
        from: ids.creator,
        operation: ids.shorten,
        request: { url: 'https://example.com/spring-sale' },
        response: { code: 'spring', shortUrl: 'https://sho.rt/spring' },
        then: [
          {
            type: 'write',
            id: 'save-link',
            by: ids.linksApi,
            store: ids.linksDb,
            dataset: 'links',
            change: { code: 'spring', url: 'https://example.com/spring-sale' },
          },
          {
            type: 'write',
            id: 'cache-link',
            by: ids.linksApi,
            store: ids.linkCache,
            dataset: 'links',
            change: { code: 'spring', url: 'https://example.com/spring-sale' },
          },
        ],
        onError: [],
      },
    ],
    faults: [],
    assertions: [
      { type: 'outcome', id: 'link-is-created', step: 'shorten', expected: 'Succeeded' },
      { type: 'event-count', id: 'link-is-stored-and-cached', kind: 'DataWritten', target: null, expected: 2 },
    ],
  },
}

// The stress profile of "Follow a short link": a campaign link shared at 9:00. Every number is declared.
const profile = {
  name: 'Campaign launch',
  scenarioId: ids.scenarioFollow,
  seed: '7',
  arrivals: { kind: 'ramp', ratePerSecond: 50, toRatePerSecond: 400, durationSeconds: 120, warmUpSeconds: 10 },
  resources: [
    { elementId: ids.linksApi, concurrency: 16, queueCapacity: 500 },
    { elementId: ids.linksDb, concurrency: 8, queueCapacity: 500 },
    { elementId: ids.clicksDb, concurrency: 4, queueCapacity: null },
  ],
  serviceTimes: [
    { target: ids.follow, time: { kind: 'lognormal', medianMs: 3, p95Ms: 12 } },
    { target: ids.linksDb, time: { kind: 'lognormal', medianMs: 12, p95Ms: 40 } },
    { target: ids.clicksDb, time: { kind: 'exponential', meanMs: 2 } },
    { target: ids.analyticsSub, time: { kind: 'uniform', minMs: 1, maxMs: 4 } },
  ],
  subscriptions: [{ subscriptionId: ids.analyticsSub, consumers: 4, queueCapacity: null }],
  branches: [],
  overrides: [],
  outages: [],
}

// --- project -------------------------------------------------------------------------------

const { post } = await startProject({
  id: ids.project,
  name: 'URL shortener',
  description: 'Short links: the Links API stores a link, redirects a visitor and sends each click to Analytics.',
})

// --- schemas -------------------------------------------------------------------------------

const code = { type: 'string', pattern: '^[a-z0-9-]{3,32}$' }
const url = { type: 'string', pattern: '^https?://' }
const instant = { type: 'string', minLength: 20 }
await post('schemas', { id: ids.createLink, name: 'CreateLink', description: 'The body of POST /links.', content: schema({ url }, ['url']) })
await post('schemas', { id: ids.linkCreated, name: 'LinkCreated', description: 'The answer of POST /links.', content: schema({ code, shortUrl: url }, ['code', 'shortUrl']) })
await post('schemas', { id: ids.redirectRequest, name: 'RedirectRequest', description: 'The path parameter of GET /{code}.', content: schema({ code }, ['code']) })
await post('schemas', { id: ids.redirect, name: 'Redirect', description: 'The answer of GET /{code}: a 301 to the long URL.', content: schema({ location: url }, ['location']) })
await post('schemas', { id: ids.linkClickedSchema, name: 'LinkClicked', description: 'One visit of a short link.', content: schema({ code, at: instant }, ['code', 'at']) })

// --- elements and relationships ------------------------------------------------------------

const element = (elementId, kind, name, more = {}) => post('elements', { id: elementId, kind, name, tags: [], ...more })
await element(ids.shortener, 'system', 'Shortener', { description: 'Makes short links and redirects visitors to the long URL.' })
await element(ids.creator, 'actor', 'Creator', { description: 'A person who makes a short link.' })
await element(ids.visitor, 'actor', 'Visitor', { description: 'A person who opens a short link.' })
await element(ids.linksApi, 'app', 'Links API', { description: 'Creates short links and answers each visit with a redirect.', technology: 'Go', parentId: ids.shortener })
await element(ids.linksDb, 'store', 'Links DB', { description: 'Holds each link and when it was last visited.', technology: 'PostgreSQL', parentId: ids.shortener })
await element(ids.linkCache, 'store', 'Link cache', { description: 'Holds recent links in memory.', technology: 'Redis', parentId: ids.shortener })
await element(ids.analytics, 'app', 'Analytics', { description: 'Counts the clicks of each link.', technology: 'Go', parentId: ids.shortener })
await element(ids.clicksDb, 'store', 'Clicks DB', { description: 'Holds one row for each click.', technology: 'ClickHouse', parentId: ids.shortener })

const relationship = (relationshipId, fromId, toId, label, technology) => post('relationships', { id: relationshipId, fromId, toId, label, technology })
await relationship(ids.relCreatorApi, ids.creator, ids.linksApi, 'creates short links', 'HTTPS')
await relationship(ids.relVisitorApi, ids.visitor, ids.linksApi, 'follows short links', 'HTTPS')
await relationship(ids.relApiDb, ids.linksApi, ids.linksDb, 'reads and writes', 'SQL')
await relationship(ids.relApiCache, ids.linksApi, ids.linkCache, 'caches links', 'RESP')
await relationship(ids.relApiAnalytics, ids.linksApi, ids.analytics, 'sends clicks', 'AMQP')
await relationship(ids.relAnalyticsClicks, ids.analytics, ids.clicksDb, 'writes clicks', 'SQL')

// --- contracts -----------------------------------------------------------------------------

await post('operations', { id: ids.shorten, ownerId: ids.linksApi, name: 'POST /links', transport: 'http', requestSchemaId: ids.createLink, responseSchemaId: ids.linkCreated, timeoutMs: 2000 })
await post('operations', { id: ids.follow, ownerId: ids.linksApi, name: 'GET /{code}', transport: 'http', requestSchemaId: ids.redirectRequest, responseSchemaId: ids.redirect, timeoutMs: 500 })

await post('channels', { id: ids.clicks, name: 'link-clicks', kind: 'queue' })
await post('channels', { id: ids.clicksDeadLetters, name: 'link-clicks.dlq', kind: 'queue' })

await post('subscriptions', { id: ids.analyticsSub, channelId: ids.clicks, consumerId: ids.analytics, retry: { maxAttempts: 3, backoffMs: 200 }, deadLetterChannelId: ids.clicksDeadLetters })

await post('messages', { id: ids.linkClicked, name: 'LinkClicked', description: 'A visitor opened a short link.', producerId: ids.linksApi, consumerIds: [ids.analytics], schemaId: ids.linkClickedSchema, tags: [] })

// --- scenarios, the stress profile and the files ------------------------------------------

await finishProject(here, `/api/projects/${ids.project}`, scenarios, profile)
