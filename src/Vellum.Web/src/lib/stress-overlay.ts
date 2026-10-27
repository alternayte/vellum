import type { ResourceStats, StressProfile, StressResult } from '@/lib/stress-api'

/**
 * What a stored Stress run draws on the canvas: the load of each card, the rate of each line
 * and the headline numbers under the canvas. It is a projection of the stored run onto the
 * cards and lines of the view. Every number is the engine's estimate; nothing here is a model
 * fact and nothing is written back.
 */

/** A card is busy from this share of worker time. The saturation rule of the engine decides "saturated". */
export const BUSY_FROM = 0.7

export type LoadStatus = 'ok' | 'busy' | 'saturated' | 'down'

const RANK: Record<LoadStatus, number> = { ok: 0, busy: 1, saturated: 2, down: 3 }

/** One pool of workers of the run: an element that serves calls or writes, or the consumers of a subscription. */
export interface PoolLoad {
  id: string
  name: string
  kind: 'element' | 'subscription'
  /** The element that holds the workers: the element itself, or the consumer of the subscription. */
  elementId: string
  utilisation: number
  /** Null in a result of engine version 1, which has no saturation rule of this kind. */
  saturated: boolean | null
  status: LoadStatus
}

export interface CardLoad {
  /** The highest utilisation of the pools on the card. */
  utilisation: number
  /** The worst status of the pools on the card. */
  status: LoadStatus
  pools: PoolLoad[]
  /** True when some pools belong to elements inside the card, not to the card itself. */
  inside: boolean
  /** The card's own element serves work in the run and is not down, so it can be failed. */
  failable: boolean
}

export interface OverlayElement {
  id: string
  parentId: string | null
}

export interface OverlayRelationship {
  id: string
  fromId: string
  toId: string
}

/** A line of the view: a relationship drawn as itself, or a derived line with its relationships. */
export interface OverlayLine {
  id: string
  relationshipIds: string[]
}

export interface StressOverlay {
  cards: Map<string, CardLoad>
  /** Estimated work per second on each line, by line ID. A line with no traffic in the run has no entry. */
  lines: Map<string, number>
}

/**
 * The elements that are down for the whole measured window by an outage of the profile. A
 * failed run ("fail this card") has one such outage.
 */
export function downElements(profile: Pick<StressProfile, 'outages'>, result: Pick<StressResult, 'window'>): Set<string> {
  const from = result.window.warmUpMs / 1000
  const to = result.window.durationMs / 1000
  return new Set(profile.outages.filter((o) => o.fromSeconds <= from && o.toSeconds >= to).map((o) => o.target))
}

function poolsOf(result: StressResult, down: ReadonlySet<string>): PoolLoad[] {
  const pool = (r: ResourceStats, elementId: string): PoolLoad => {
    const saturated = r.saturation ? r.saturation.saturated : null
    const status: LoadStatus = down.has(elementId) || down.has(r.id)
      ? 'down'
      : saturated
        ? 'saturated'
        : r.utilisation >= BUSY_FROM ? 'busy' : 'ok'
    return { id: r.id, name: r.name, kind: r.kind, elementId, utilisation: r.utilisation, saturated, status }
  }
  return [
    ...result.elements.map((r) => pool(r, r.id)),
    // A subscription of a result before engine version 4 names no consumer, so it has no card.
    ...result.subscriptions.filter((r) => r.consumerId).map((r) => pool(r, r.consumerId!)),
  ]
}

/** The overlay of one stored run on the cards and lines of a view. */
export function stressOverlay(
  run: { profile: Pick<StressProfile, 'outages'>; result: StressResult },
  elements: readonly OverlayElement[],
  relationships: readonly OverlayRelationship[],
  lines: readonly OverlayLine[],
  visible: ReadonlySet<string>,
): StressOverlay {
  const parentOf = new Map(elements.map((e) => [e.id, e.parentId]))
  /** The element and its ancestors, the element first. The walk stops at a card it saw before. */
  const chain = (id: string): string[] => {
    const seen: string[] = []
    for (let at: string | null | undefined = id; at != null && !seen.includes(at); at = parentOf.get(at)) {
      seen.push(at)
      if (!parentOf.has(at)) break
    }
    return seen
  }

  const down = downElements(run.profile, run.result)
  const cards = new Map<string, CardLoad>()
  for (const pool of poolsOf(run.result, down)) {
    const card = chain(pool.elementId).find((id) => visible.has(id))
    if (!card) continue
    const current = cards.get(card)
    if (!current) {
      cards.set(card, { utilisation: pool.utilisation, status: pool.status, pools: [pool], inside: false, failable: false })
      continue
    }
    current.pools.push(pool)
    current.utilisation = Math.max(current.utilisation, pool.utilisation)
    if (RANK[pool.status] > RANK[current.status]) current.status = pool.status
  }
  for (const [card, load] of cards) {
    const own = load.pools.filter((p) => p.elementId === card)
    load.inside = own.length < load.pools.length
    // An element that is down already cannot be failed again.
    load.failable = own.length > 0 && !down.has(card)
  }

  const rates = new Map<string, number>()
  const traffic = run.result.traffic ?? []
  if (traffic.length > 0) {
    const relationshipById = new Map(relationships.map((r) => [r.id, r]))
    const ancestors = new Map<string, Set<string>>()
    const within = (outer: string, inner: string) => {
      let set = ancestors.get(inner)
      if (!set) ancestors.set(inner, (set = new Set(chain(inner))))
      return set.has(outer)
    }
    for (const line of lines) {
      const covering = line.relationshipIds.map((id) => relationshipById.get(id)).filter((r): r is OverlayRelationship => !!r)
      // Each pair of the run counts once on a line, however many of its relationships cover it.
      let sum = 0
      for (const t of traffic) {
        if (covering.some((r) => within(r.fromId, t.fromId) && within(r.toId, t.toId))) sum += t.perSecond
      }
      if (sum > 0) rates.set(line.id, sum)
    }
  }

  return { cards, lines: rates }
}

export interface StressStrip {
  /** Flows that started per second of the measured window. */
  offeredPerSecond: number
  /** Flows that completed with success per second of the measured window. */
  goodputPerSecond: number
  /** Completed flows of the finished ones; null when no flow finished. */
  successRate: number | null
  p95Ms: number | null
  busiest: { id: string; name: string; utilisation: number } | null
  /** Null in a result of engine version 1 to 3, which has no estimate. */
  limit: { flowsPerSecond: number | null; name: string | null; rule: string } | null
}

/** The six headline numbers of a run, from the stored result. */
export function stressStrip(result: StressResult): StressStrip {
  const windowSeconds = (result.window.durationMs - result.window.warmUpMs) / 1000
  const { completed, failed } = result.flow.counts
  const busiest = [...result.elements, ...result.subscriptions].reduce<ResourceStats | null>(
    (best, r) => (best === null || r.utilisation > best.utilisation ? r : best),
    null,
  )
  return {
    offeredPerSecond: windowSeconds > 0 ? result.flow.arrivals / windowSeconds : 0,
    goodputPerSecond: result.flow.throughputPerSecond,
    successRate: completed + failed > 0 ? completed / (completed + failed) : null,
    p95Ms: result.flow.latency.p95Ms,
    busiest: busiest ? { id: busiest.id, name: busiest.name, utilisation: busiest.utilisation } : null,
    limit: result.capacity ? { flowsPerSecond: result.capacity.flowsPerSecond, name: result.capacity.name, rule: result.capacity.rule } : null,
  }
}

/** A rate per second in few characters: 0.42, 8.5, 140, 1.2k, 38.6k. */
export function formatRate(value: number): string {
  if (value === 0) return '0'
  if (value >= 1000) return `${(value / 1000).toFixed(value >= 100_000 ? 0 : 1)}k`
  if (value >= 100) return value.toFixed(0)
  if (value >= 10) return value.toFixed(1).replace(/\.0$/, '')
  return value.toFixed(value >= 1 ? 1 : 2)
}
