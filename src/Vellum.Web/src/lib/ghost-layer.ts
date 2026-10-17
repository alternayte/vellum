import { cardSize } from '@/lib/card-size'
import { BOUNDARY_INSET, computeLayout, type Viewport } from '@/lib/layout'
import type { ArchitectureItem, ArchitectureProposal } from '@/lib/design-api'

/**
 * The ghost layer: the pending items of an architecture proposal drawn over the current view
 * as dashed cards and lines. It is a projection of the proposal. Nothing of it is in the model
 * until a person accepts an item; the accepted item then comes back from the model as a real card.
 */

/** The canvas ID of the ghost of an item. An accepted item has the item's ID in the model, so the ghost never takes it. */
export const ghostNodeId = (itemId: string) => `ghost:${itemId}`
export const isGhostNodeId = (id: string) => id.startsWith('ghost:')
export const itemIdOf = (nodeId: string) => nodeId.slice('ghost:'.length)

/** The pending items and the items they depend on, directly or through others, that are pending too. */
export function withDependencies(ids: Iterable<string>, items: ArchitectureItem[]): Set<string> {
  const byId = new Map(items.map((item) => [item.id, item]))
  const result = new Set<string>()
  const queue = [...ids].filter((id) => byId.get(id)?.status === 'pending')
  for (const id of queue) result.add(id)
  while (queue.length > 0) {
    for (const dependency of byId.get(queue.pop()!)?.dependsOn ?? []) {
      if (!result.has(dependency) && byId.get(dependency)?.status === 'pending') {
        result.add(dependency)
        queue.push(dependency)
      }
    }
  }
  return result
}

/** The given items and every item that depends on one of them, directly or through others. */
export function withDependants(ids: Iterable<string>, items: ArchitectureItem[]): Set<string> {
  const result = new Set(ids)
  for (let changed = true; changed;) {
    changed = false
    for (const item of items) {
      if (!result.has(item.id) && item.dependsOn.some((d) => result.has(d))) {
        result.add(item.id)
        changed = true
      }
    }
  }
  return result
}

/** What a reject of one item takes: the item and the pending items that need it. They cannot be accepted without it. */
export function rejectClosure(ids: Iterable<string>, items: ArchitectureItem[]): Set<string> {
  const pending = new Set(items.filter((item) => item.status === 'pending').map((item) => item.id))
  return new Set([...withDependants(ids, items)].filter((id) => pending.has(id)))
}

export interface GhostElement {
  id: string
  kind: string
  name: string
  technology: string | null
  /** A real element, another item, or null for the top level. */
  parentId: string | null
}

export interface GhostRelationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  relationshipKind: string | null
}

const text = (value: unknown): string | null => (typeof value === 'string' && value.length > 0 ? value : null)

/** The pending elements and relationships of a proposal, which the canvas can draw, and the rest. */
export function ghostParts(proposal: ArchitectureProposal): { elements: GhostElement[]; relationships: GhostRelationship[]; others: ArchitectureItem[] } {
  const elements: GhostElement[] = []
  const relationships: GhostRelationship[] = []
  const others: ArchitectureItem[] = []
  for (const item of proposal.items) {
    if (item.status !== 'pending') continue
    const p = item.proposed
    if (item.entityKind === 'element' && text(p.kind)) {
      elements.push({ id: item.id, kind: text(p.kind)!, name: item.name, technology: text(p.technology), parentId: text(p.parentId) })
    } else if (item.entityKind === 'relationship' && text(p.fromId) && text(p.toId)) {
      relationships.push({ id: item.id, fromId: text(p.fromId)!, toId: text(p.toId)!, label: text(p.label), relationshipKind: text(p.kind) })
    } else {
      others.push(item)
    }
  }
  return { elements, relationships, others }
}

interface RealElement {
  id: string
  kind: string
  parentId: string | null
}

export interface GhostPlan {
  /** Each ghost element on the canvas, with the node it sits in: null for the level, a real element, or another ghost. */
  hosts: Map<string, string | null>
  /** Open cards of this level that hold a ghost. Each one grows to hold its ghosts. */
  realHosts: Set<string>
  /** Ghosts that hold another ghost. They draw as a frame around it. */
  ghostHosts: Set<string>
  /** Ghost relationships whose two ends are both on the canvas, by canvas node ID. */
  edges: { id: string; source: string; target: string }[]
  /** Pending elements and relationships that are not on this level of the canvas. */
  elsewhere: string[]
}

/**
 * Where each ghost goes on the current level. A ghost whose parent is the level, an open card
 * of the level, or a ghost on the canvas is shown. A ghost inside a closed card is not: the view
 * does not nest there, and the bar lists it. A ghost relationship shows when both of its ends
 * are on the canvas.
 */
export function planGhosts(
  ghosts: { elements: GhostElement[]; relationships: GhostRelationship[] },
  elements: RealElement[],
  currentRootId: string | null,
  expanded: ReadonlySet<string>,
): GhostPlan {
  const onLevel = new Set(elements.filter((e) => (e.parentId ?? null) === currentRootId).map((e) => e.id))
  const hosts = new Map<string, string | null>()
  const realHosts = new Set<string>()
  for (let changed = true; changed;) {
    changed = false
    for (const ghost of ghosts.elements) {
      if (hosts.has(ghost.id)) continue
      const parent = ghost.parentId
      let host: string | null | undefined
      if (parent === currentRootId) host = null
      // A card of the level holds a ghost only while it is open: the view nests there already.
      else if (parent !== null && onLevel.has(parent) && expanded.has(parent)) host = parent
      else if (parent !== null && hosts.has(parent)) host = parent
      if (host === undefined) continue
      hosts.set(ghost.id, host)
      if (host !== null && onLevel.has(host)) realHosts.add(host)
      changed = true
    }
  }
  const ghostHosts = new Set([...hosts.values()].filter((h): h is string => h !== null && hosts.has(h)))

  // The real cards on the canvas: those of the level, and the children of each open card.
  const open = new Set([...expanded, ...realHosts])
  const realShown = new Set(onLevel)
  for (const e of elements) if (e.parentId && onLevel.has(e.parentId) && open.has(e.parentId)) realShown.add(e.id)
  const nodeOf = (id: string): string | null => (hosts.has(id) ? ghostNodeId(id) : realShown.has(id) ? id : null)

  const edges: GhostPlan['edges'] = []
  const elsewhere = ghosts.elements.filter((g) => !hosts.has(g.id)).map((g) => g.id)
  for (const rel of ghosts.relationships) {
    const source = nodeOf(rel.fromId)
    const target = nodeOf(rel.toId)
    if (source && target && source !== target) edges.push({ id: rel.id, source, target })
    else elsewhere.push(rel.id)
  }
  return { hosts, realHosts, ghostHosts, edges, elsewhere }
}

export interface Box {
  x: number
  y: number
  width: number
  height: number
}

/** Where the canvas puts a card of a level that has no saved place: a grid of three columns. */
export const defaultCardPosition = (index: number) => ({ x: (index % 3) * 340, y: Math.floor(index / 3) * 200 })
/** Where the canvas puts a child without a saved place inside its open parent. */
export const defaultChildPosition = (index: number) => ({ x: 40 + (index % 3) * 300, y: 60 + Math.floor(index / 3) * 180 })
/** The size of an open parent without a saved size: the default grid of its children. */
export function defaultContainerSize(childCount: number) {
  const columns = Math.min(3, Math.max(1, childCount))
  const rows = Math.max(1, Math.ceil(childCount / 3))
  return { width: 40 + columns * 300 + 20, height: 60 + rows * 180 + 20 }
}

/** The place of a ghost. A ghost inside a node is placed from that node's corner, as a child on the canvas is. */
export interface GhostPlace {
  x: number
  y: number
  /** Set for a ghost that holds other ghosts. */
  width?: number
  height?: number
}

export interface GhostPlaces {
  ghosts: Map<string, GhostPlace>
  /** The size that a card of the level needs while it is open around its ghosts. */
  hostSizes: Map<string, { width: number; height: number }>
  /**
   * The cards of the level that have no saved place, where the canvas draws them now. The canvas
   * draws such a card on a grid by its order, which a new card changes; an accept saves these
   * places, so the cards stay where the ghosts were placed beside them.
   */
  unsaved: Map<string, { x: number; y: number }>
}

const GAP = 32
const STEP = 48
const RINGS = 40
const SNAP = 16

const overlaps = (a: Box, b: Box) =>
  a.x < b.x + b.width + GAP && b.x < a.x + a.width + GAP && a.y < b.y + b.height + GAP && b.y < a.y + a.height + GAP

const snap = (v: number) => Math.round(v / SNAP) * SNAP

/** The free spot nearest to `target` where a box of `size` touches none of `taken`, searched in rings around it. */
export function freeSpot(target: { x: number; y: number }, size: { width: number; height: number }, taken: Box[]): { x: number; y: number } {
  const at = (x: number, y: number) => ({ x: snap(x), y: snap(y), ...size })
  const start = at(target.x, target.y)
  if (!taken.some((b) => overlaps(start, b))) return { x: start.x, y: start.y }
  for (let ring = 1; ring <= RINGS; ring++) {
    const r = ring * STEP
    const candidates: { x: number; y: number }[] = []
    for (let d = -r; d <= r; d += STEP) {
      candidates.push({ x: target.x + d, y: target.y - r }, { x: target.x + d, y: target.y + r })
      if (Math.abs(d) !== r) candidates.push({ x: target.x - r, y: target.y + d }, { x: target.x + r, y: target.y + d })
    }
    candidates.sort((a, b) => Math.hypot(a.x - target.x, a.y - target.y) - Math.hypot(b.x - target.x, b.y - target.y))
    for (const c of candidates) {
      const box = at(c.x, c.y)
      if (!taken.some((b) => overlaps(box, b))) return { x: box.x, y: box.y }
    }
  }
  return { x: start.x, y: start.y }
}

function median(values: number[]): number {
  if (values.length === 0) return 0
  const sorted = [...values].sort((a, b) => a - b)
  const mid = Math.floor(sorted.length / 2)
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2
}

interface SavedPosition {
  elementId: string
  x: number
  y: number
  width?: number
  height?: number
}

/**
 * Places the ghosts with the auto layout of Tidy, relative to the real cards. The layout runs
 * over the real cards and the ghosts of the level together; each group of ghosts then moves by
 * the offset that brings the real cards of its group onto their places on the canvas, and a
 * ghost that would lie over a card goes to the nearest free spot. Real cards do not move.
 */
export async function placeGhosts(
  plan: GhostPlan,
  ghosts: { elements: GhostElement[]; relationships: GhostRelationship[] },
  real: { elements: RealElement[]; relationships: { id: string; fromId: string; toId: string }[]; positions: SavedPosition[] },
  currentRootId: string | null,
  expanded: ReadonlySet<string>,
  viewport?: Viewport,
): Promise<GhostPlaces> {
  const saved = new Map(real.positions.map((p) => [p.elementId, p]))
  const level = real.elements.filter((e) => (e.parentId ?? null) === currentRootId)
  const open = new Set([...[...expanded].filter((id) => level.some((e) => e.id === id)), ...plan.realHosts])
  const childrenOf = (id: string) => real.elements.filter((e) => e.parentId === id)

  // The real cards where the canvas draws them, from their saved places or the default grid.
  const actual = new Map<string, Box>()
  const unsaved = new Map<string, { x: number; y: number }>()
  level.forEach((e, index) => {
    const p = saved.get(e.id)
    const at = p ? { x: p.x, y: p.y } : defaultCardPosition(index)
    if (!p) unsaved.set(e.id, at)
    const size = open.has(e.id)
      ? { width: p?.width ?? defaultContainerSize(childrenOf(e.id).length).width, height: p?.height ?? defaultContainerSize(childrenOf(e.id).length).height }
      : cardSize(e.kind)
    actual.set(e.id, { ...at, ...size })
    if (open.has(e.id)) {
      childrenOf(e.id).forEach((child, ci) => {
        const c = saved.get(child.id)
        actual.set(child.id, { ...(c ? { x: c.x, y: c.y } : defaultChildPosition(ci)), ...cardSize(child.kind) })
      })
    }
  })

  // One layout of the real cards and the ghosts of this level, every group open that holds something.
  const ghostIds = new Set(plan.hosts.keys())
  const layoutElements = [
    ...level.map((e) => ({ id: e.id, parentId: null, ...cardSize(e.kind) })),
    ...real.elements.filter((e) => e.parentId && open.has(e.parentId)).map((e) => ({ id: e.id, parentId: e.parentId, ...cardSize(e.kind) })),
    ...ghosts.elements.filter((g) => ghostIds.has(g.id)).map((g) => ({ id: g.id, parentId: plan.hosts.get(g.id) ?? null, ...cardSize(g.kind) })),
  ]
  const shownIds = new Set(layoutElements.map((e) => e.id))
  const layoutRelationships = [
    ...real.relationships,
    ...ghosts.relationships.map((r) => ({ id: r.id, fromId: r.fromId, toId: r.toId })),
  ].filter((r) => shownIds.has(r.fromId) && shownIds.has(r.toId))
  const layoutOpen = new Set([...open, ...plan.ghostHosts])
  const laid = new Map((await computeLayout(layoutElements, layoutRelationships, viewport, layoutOpen)).map((p) => [p.elementId, p]))

  const result = new Map<string, GhostPlace>()
  const hostSizes = new Map<string, { width: number; height: number }>()
  const sizeOf = (g: GhostElement): { width: number; height: number } => {
    const p = laid.get(g.id)
    return plan.ghostHosts.has(g.id) && p?.width && p?.height ? { width: p.width, height: p.height } : cardSize(g.kind)
  }
  const ghostsIn = (host: string | null) => ghosts.elements.filter((g) => ghostIds.has(g.id) && (plan.hosts.get(g.id) ?? null) === host)

  // Inside a ghost: the layout as it is, since everything there is new.
  for (const host of plan.ghostHosts) {
    for (const g of ghostsIn(host)) {
      const p = laid.get(g.id)
      if (p) result.set(g.id, { x: p.x, y: p.y, ...(plan.ghostHosts.has(g.id) ? sizeOf(g) : {}) })
    }
  }

  // Places a group of ghosts beside real cards: moved by the offset of the real cards, then off them.
  const placeGroup = (members: GhostElement[], realIds: string[], taken: Box[], origin: { x: number; y: number }) => {
    const anchored = realIds.filter((id) => laid.has(id) && actual.has(id))
    const dx = anchored.length > 0 ? median(anchored.map((id) => actual.get(id)!.x - laid.get(id)!.x)) : origin.x
    const dy = anchored.length > 0 ? median(anchored.map((id) => actual.get(id)!.y - laid.get(id)!.y)) : origin.y
    for (const g of members) {
      const p = laid.get(g.id)
      if (!p) continue
      const size = sizeOf(g)
      const spot = freeSpot({ x: p.x + dx, y: p.y + dy }, size, taken)
      taken.push({ ...spot, ...size })
      result.set(g.id, { ...spot, ...(plan.ghostHosts.has(g.id) ? size : {}) })
    }
  }

  // Inside an open card of the level: below its real children, in the arrangement of the layout,
  // and the card grows to hold them all. The places of real children are shared with the level
  // inside the card, so the ghosts do not take their offset.
  for (const host of open) {
    const members = ghostsIn(host)
    const taken = childrenOf(host).map((c) => actual.get(c.id)!).filter(Boolean)
    if (members.length > 0) {
      const left = taken.length > 0 ? Math.min(...taken.map((b) => b.x)) : BOUNDARY_INSET.x
      const top = taken.length > 0 ? Math.max(...taken.map((b) => b.y + b.height)) + STEP : BOUNDARY_INSET.y
      const laidMembers = members.map((g) => laid.get(g.id)).filter((p) => !!p)
      const minX = Math.min(...laidMembers.map((p) => p.x))
      const minY = Math.min(...laidMembers.map((p) => p.y))
      placeGroup(members, [], taken, { x: left - minX, y: top - minY })
    }
    const box = actual.get(host)!
    let right = box.width
    let bottom = box.height
    for (const b of [...taken, ...members.map((g) => ({ ...result.get(g.id)!, ...sizeOf(g) }))]) {
      right = Math.max(right, b.x + b.width + BOUNDARY_INSET.right)
      bottom = Math.max(bottom, b.y + b.height + BOUNDARY_INSET.bottom)
    }
    if (right !== box.width || bottom !== box.height) {
      hostSizes.set(host, { width: Math.round(right), height: Math.round(bottom) })
      actual.set(host, { ...box, width: Math.round(right), height: Math.round(bottom) })
    }
  }

  // The level: beside the real cards of the level. With no real card, the ghosts start at the corner.
  placeGroup(ghostsIn(null), level.map((e) => e.id), level.map((e) => actual.get(e.id)!), { x: 0, y: 0 })
  return { ghosts: result, hostSizes, unsaved }
}

/** What a ghost card, a ghost line and a row of the layer show of one item. */
export interface GhostNodeData extends Record<string, unknown> {
  itemId: string
  kind: string
  name: string
  technology: string | null
  alias: string
  confidence: number
  /** Why the server refused the last accept of this item. */
  refused: string[]
  /** Why the item cannot be accepted against the model as it is now. */
  issues: string[]
  /** How many other pending items an accept takes along, and a reject. */
  needs: number
  neededBy: number
  canDecide: boolean
}

/** The data of the ghost of each item of a proposal. */
export function ghostItemData(
  proposal: ArchitectureProposal,
  refusals: ReadonlyMap<string, string[]> | null,
  canDecide: boolean,
): (itemId: string) => GhostNodeData {
  const items = proposal.items
  const byId = new Map(items.map((item) => [item.id, item]))
  const issues = new Map<string, string[]>()
  for (const issue of proposal.itemIssues) issues.set(issue.itemId, [...(issues.get(issue.itemId) ?? []), issue.reason])
  return (itemId) => {
    const item = byId.get(itemId)!
    return {
      itemId,
      kind: text(item.proposed.kind) ?? item.entityKind,
      name: item.name,
      technology: text(item.proposed.technology),
      alias: item.alias,
      confidence: item.confidence,
      refused: refusals?.get(itemId) ?? [],
      issues: issues.get(itemId) ?? [],
      needs: withDependencies([itemId], items).size - 1,
      neededBy: rejectClosure([itemId], items).size - 1,
      canDecide,
    }
  }
}
