import { describe, expect, it } from 'vitest'
import { projectLines } from './derived-lines'
import { formatRate, stressOverlay, stressStrip } from './stress-overlay'
import type { Outage, ResourceStats, StressResult, TrafficStats } from './stress-api'

// A shop with an API and a database inside it, a billing app that consumes messages, and a customer outside.
const elements = [
  { id: 'customer', parentId: null },
  { id: 'shop', parentId: null },
  { id: 'api', parentId: 'shop' },
  { id: 'db', parentId: 'shop' },
  { id: 'billing', parentId: 'shop' },
]
const relationships = [
  { id: 'r-places', fromId: 'customer', toId: 'api' },
  { id: 'r-writes', fromId: 'api', toId: 'db' },
  { id: 'r-bills', fromId: 'api', toId: 'billing' },
  // A second relationship over the same pair: the traffic counts once on a line that holds both.
  { id: 'r-reads', fromId: 'api', toId: 'db' },
  { id: 'r-idle', fromId: 'customer', toId: 'billing' },
]

const resource = (id: string, utilisation: number, saturated: boolean | null, extra: Partial<ResourceStats> = {}) =>
  ({ id, name: id, kind: 'element', utilisation, saturation: saturated === null ? null : { saturated }, ...extra }) as unknown as ResourceStats

const traffic = (fromId: string, toId: string, kind: TrafficStats['kind'], perSecond: number): TrafficStats =>
  ({ label: 'estimated', fromId, toId, kind, sent: perSecond * 50, perSecond })

function result(over: Partial<StressResult> = {}): StressResult {
  return {
    window: { warmUpMs: 10_000, durationMs: 60_000, endedAtMs: 61_000 },
    flow: { label: 'estimated', arrivals: 2000, counts: { completed: 1900, failed: 100 }, throughputPerSecond: 38, latency: { p95Ms: 93.7 } },
    elements: [resource('api', 0.42, false), resource('db', 0.81, false)],
    subscriptions: [resource('sub-billing', 1, true, { kind: 'subscription', name: 'Billing on orders', consumerId: 'billing' })],
    traffic: [traffic('customer', 'api', 'call', 40), traffic('api', 'db', 'write', 40), traffic('api', 'billing', 'message', 40)],
    capacity: { label: 'estimated', flowsPerSecond: 33.3, id: 'sub-billing', name: 'Billing on orders', kind: 'subscription', rule: 'rule' },
    ...over,
  } as unknown as StressResult
}

const noOutages: { outages: Outage[] } = { outages: [] }

function overlay(visible: string[], run = { profile: noOutages, result: result() }) {
  const set = new Set(visible)
  const { direct, derived } = projectLines(elements, relationships, set)
  const lines = [...direct.map((r) => ({ id: r.id, relationshipIds: [r.id] })), ...derived]
  return stressOverlay(run, elements, relationships, lines, set)
}

describe('stressOverlay', () => {
  it('puts each pool on its own card inside an open system, with the status of the engine', () => {
    const { cards } = overlay(['customer', 'shop', 'api', 'db', 'billing'])
    expect(cards.get('api')).toMatchObject({ utilisation: 0.42, status: 'ok', inside: false, failable: true })
    // 81 % of worker time is busy; the saturation rule did not name it.
    expect(cards.get('db')?.status).toBe('busy')
    // The consumer's card carries the subscription it consumes.
    expect(cards.get('billing')).toMatchObject({ utilisation: 1, status: 'saturated', failable: true })
    expect(cards.has('customer')).toBe(false)
    expect(cards.has('shop')).toBe(false)
  })

  it('rolls the pools inside a closed card up to it, worst status and highest utilisation, and offers no failure', () => {
    const { cards } = overlay(['customer', 'shop'])
    const shop = cards.get('shop')!
    expect(shop).toMatchObject({ utilisation: 1, status: 'saturated', inside: true, failable: false })
    expect(shop.pools.map((p) => p.id)).toEqual(['api', 'db', 'sub-billing'])
  })

  it('gives each line the traffic of the run between its ends, and nothing to a line with no traffic', () => {
    const { lines } = overlay(['customer', 'shop', 'api', 'db', 'billing'])
    expect(lines.get('r-places')).toBe(40)
    expect(lines.get('r-writes')).toBe(40)
    expect(lines.get('r-reads')).toBe(40)
    expect(lines.get('r-bills')).toBe(40)
    expect(lines.has('r-idle')).toBe(false)
  })

  it('sums the traffic under a derived line, each pair once', () => {
    // Closed: customer → shop holds r-places and r-idle; only the call to the API carries traffic.
    const { lines } = overlay(['customer', 'shop'])
    expect(lines.get('derived:customer>shop')).toBe(40)
    expect(lines.size).toBe(1)
  })

  it('marks an element that is down for the whole window, and does not offer to fail it again', () => {
    const run = { profile: { outages: [{ target: 'db', fromSeconds: 0, toSeconds: 3660 }] }, result: result() }
    const { cards } = overlay(['customer', 'shop', 'api', 'db', 'billing'], run)
    expect(cards.get('db')).toMatchObject({ status: 'down', failable: false })
    // An outage of a part of the window is no failed card.
    const partial = { profile: { outages: [{ target: 'db', fromSeconds: 20, toSeconds: 30 }] }, result: result() }
    expect(overlay(['customer', 'shop', 'api', 'db', 'billing'], partial).cards.get('db')?.status).toBe('busy')
  })

  it('reads a run stored before engine version 4: loads by element, no line rates, no consumer cards', () => {
    const old = result({ traffic: undefined, capacity: undefined, subscriptions: [resource('sub-billing', 1, null, { kind: 'subscription' })] })
    const { cards, lines } = overlay(['customer', 'shop', 'api', 'db', 'billing'], { profile: noOutages, result: old })
    expect(lines.size).toBe(0)
    expect(cards.has('billing')).toBe(false)
    expect(cards.get('api')?.status).toBe('ok')
    expect(stressStrip(old).limit).toBeNull()
  })
})

describe('stressStrip', () => {
  it('gives the six headline numbers of the run', () => {
    expect(stressStrip(result())).toEqual({
      offeredPerSecond: 40,
      goodputPerSecond: 38,
      successRate: 0.95,
      p95Ms: 93.7,
      busiest: { id: 'sub-billing', name: 'Billing on orders', utilisation: 1 },
      limit: { flowsPerSecond: 33.3, name: 'Billing on orders', rule: 'rule' },
    })
  })
})

describe('formatRate', () => {
  it('keeps a rate short', () => {
    expect([0.42, 8.46, 12, 12.5, 140.2, 1234, 38_600, 250_000].map(formatRate)).toEqual(['0.42', '8.5', '12', '12.5', '140', '1.2k', '38.6k', '250k'])
  })
})
