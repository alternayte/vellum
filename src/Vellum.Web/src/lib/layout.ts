import ELK, { type ElkNode } from 'elkjs/lib/elk.bundled.js'
import { projectLines } from '@/lib/derived-lines'

const elk = new ELK()

export interface LayoutElement {
  id: string
  parentId?: string | null
  width?: number
  height?: number
}

interface LayoutRelationship {
  id: string
  fromId: string
  toId: string
}

export interface LayoutPosition {
  elementId: string
  x: number
  y: number
  width?: number
  height?: number
}

/**
 * Where the children of an element start inside its boundary, and the margin the boundary
 * keeps around them. An expanded element draws its children at their saved positions relative
 * to its own corner, so a level inside an element starts below the boundary's title.
 */
export const BOUNDARY_INSET = { x: 40, y: 64, right: 40, bottom: 40 }

const DEFAULT_SIZE = { width: 264, height: 120 }

// Each level is laid out in both directions. The one that shows larger on the visible canvas
// wins: a chain of four on a wide screen goes across, not down a narrow column. Across needs
// wider gaps between layers, since a relationship label lies across the gap.
const STRAIGHT: Record<string, string>[] = [
  { 'elk.direction': 'DOWN', 'elk.layered.spacing.nodeNodeBetweenLayers': '120' },
  { 'elk.direction': 'RIGHT', 'elk.layered.spacing.nodeNodeBetweenLayers': '176' },
]
// A long chain folds into rows only when straight it would show its cards too small to read.
const WRAPPED = STRAIGHT.map((c) => ({ ...c, 'elk.layered.wrapping.strategy': 'MULTI_EDGE' }))
const LEGIBLE_ZOOM = 0.6
const MAX_ZOOM = 1
/** Space a fit keeps around a level, in screen pixels (the canvas fit uses about this). */
const FIT_MARGIN = 56

export interface Viewport {
  width: number
  height: number
}

const BASE_OPTIONS: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.spacing.nodeNode': '72',
  'elk.spacing.componentComponent': '96',
  'elk.edgeRouting': 'SPLINES',
  'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
  'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
}

/** The zoom at which a fit shows a box of this size on the viewport, at most the fit's limit. */
function fitZoom(width: number, height: number, viewport: Viewport): number {
  const zoom = Math.min((viewport.width - 2 * FIT_MARGIN) / Math.max(width, 1), (viewport.height - 2 * FIT_MARGIN) / Math.max(height, 1))
  return Math.min(MAX_ZOOM, zoom)
}

async function layoutLevel(
  level: LayoutElement[],
  relationships: LayoutRelationship[],
  viewport: Viewport,
  all: LayoutElement[],
): Promise<{ positions: LayoutPosition[]; width: number; height: number }> {
  const ids = new Set(level.map((e) => e.id))
  // A level is shaped by the lines the canvas draws on it with every card closed: the
  // relationships between its elements and the derived lines of those between their insides.
  const lines = projectLines(all.map((e) => ({ id: e.id, parentId: e.parentId ?? null })), relationships, ids)
  const edges = [...lines.direct, ...lines.derived]
    .filter((r) => r.fromId !== r.toId)
    .map((r) => ({ id: r.id, sources: [r.fromId], targets: [r.toId] }))
  const children = level.map((el) => ({
    id: el.id,
    width: el.width ?? DEFAULT_SIZE.width,
    height: el.height ?? DEFAULT_SIZE.height,
  }))

  const aspect = (viewport.width / viewport.height).toFixed(2)
  let best: { node: ElkNode; score: number } | null = null
  const consider = async (candidates: Record<string, string>[]) => {
    for (const candidate of candidates) {
      const node: ElkNode = await elk.layout({
        id: 'root',
        layoutOptions: { ...BASE_OPTIONS, ...candidate, 'elk.aspectRatio': aspect },
        children: children.map((c) => ({ ...c })),
        edges: edges.map((e) => ({ ...e })),
      })
      const score = fitZoom(node.width ?? 0, node.height ?? 0, viewport)
      // A near tie keeps the earlier candidate: along the long side of the canvas first, and
      // straight before folded.
      if (!best || score > best.score * 1.05) best = { node, score }
    }
  }
  const wide = viewport.width >= viewport.height
  const order = <T,>(list: T[]) => (wide ? [list[1], list[0]] : list)
  await consider(order(STRAIGHT))
  if (best!.score < LEGIBLE_ZOOM) await consider(order(WRAPPED))

  const node = best!.node
  const placed = node.children ?? []
  const minX = Math.min(...placed.map((c) => c.x ?? 0))
  const minY = Math.min(...placed.map((c) => c.y ?? 0))
  const positions = placed.map((c) => ({ elementId: c.id, x: Math.round((c.x ?? 0) - minX), y: Math.round((c.y ?? 0) - minY) }))
  const width = Math.max(...placed.map((c) => (c.x ?? 0) - minX + (c.width ?? 0)))
  const height = Math.max(...placed.map((c) => (c.y ?? 0) - minY + (c.height ?? 0)))
  return { positions, width, height }
}

/** The gap between what a person placed on a level and the elements placed next to it. */
const PLACE_GAP = 120

/**
 * Places the elements that have no saved position, with the same layout as `computeLayout`, and
 * moves nothing that has one. A view with no saved position is laid out whole. On a level that
 * has saved positions, the new elements of the level are laid out as one group to the right of
 * them. Returns the positions of the new elements only (with a size for a new parent).
 */
export async function placeNew(
  elements: LayoutElement[],
  relationships: LayoutRelationship[],
  saved: LayoutPosition[],
  viewport: Viewport = { width: 1392, height: 820 },
  expanded: ReadonlySet<string> = new Set(),
): Promise<LayoutPosition[]> {
  const placed = new Map(saved.map((p) => [p.elementId, p]))
  const fresh = elements.filter((e) => !placed.has(e.id))
  if (fresh.length === 0) return []
  const full = await computeLayout(elements, relationships, viewport, expanded)
  if (placed.size === 0) return full

  // A level without a saved position takes the whole layout's places; a new parent its size.
  const byId = new Map(full.map((p) => [p.elementId, p]))
  const levelOf = (e: LayoutElement) => e.parentId ?? null
  const result: LayoutPosition[] = []
  const levels = new Map<string | null, LayoutElement[]>()
  for (const el of fresh) levels.set(levelOf(el), [...(levels.get(levelOf(el)) ?? []), el])
  const view = viewport.width > 2 * FIT_MARGIN + 100 && viewport.height > 2 * FIT_MARGIN + 100 ? viewport : { width: 1392, height: 820 }
  for (const [parentId, members] of levels) {
    const kept = elements.filter((e) => levelOf(e) === parentId && placed.has(e.id)).map((e) => placed.get(e.id)!)
    if (kept.length === 0) {
      for (const el of members) result.push(byId.get(el.id)!)
      continue
    }
    const sized = members.map((el) => {
      const size = byId.get(el.id)
      return expanded.has(el.id) && size?.width && size.height ? { ...el, width: size.width, height: size.height } : el
    })
    const { positions } = await layoutLevel(sized, relationships, view, elements)
    const right = Math.max(...kept.map((p) => p.x + (p.width ?? DEFAULT_SIZE.width)))
    const top = Math.min(...kept.map((p) => p.y))
    for (const p of positions) {
      const size = byId.get(p.elementId)
      result.push({ ...p, x: p.x + right + PLACE_GAP, y: p.y + top, width: size?.width, height: size?.height })
    }
  }
  return result
}

/**
 * Lays out every level of the model on its own: the top level, and the children of each
 * element. The relationships between siblings and the derived lines between them shape a level,
 * since the canvas draws a level with only its own elements. `viewport` is the size of the visible canvas in screen pixels.
 *
 * Children are placed from `BOUNDARY_INSET`, and their parent gets the size that holds them,
 * so an expanded parent draws its children inside its boundary. An element in `expanded` is
 * shown open on its level, so its level makes room for that size, not for its card.
 */
export async function computeLayout(
  elements: LayoutElement[],
  relationships: LayoutRelationship[],
  viewport: Viewport = { width: 1392, height: 820 },
  expanded: ReadonlySet<string> = new Set(),
): Promise<LayoutPosition[]> {
  const levels = new Map<string | null, LayoutElement[]>()
  for (const el of elements) {
    const key = el.parentId ?? null
    levels.set(key, [...(levels.get(key) ?? []), el])
  }
  const view = viewport.width > 2 * FIT_MARGIN + 100 && viewport.height > 2 * FIT_MARGIN + 100 ? viewport : { width: 1392, height: 820 }

  // The deepest levels first: an open element needs the size of its inside before its own level.
  const parentOf = new Map(elements.map((e) => [e.id, e.parentId ?? null]))
  const depth = (id: string | null) => {
    let d = 0
    for (let at = id; at !== null && d <= elements.length; at = parentOf.get(at) ?? null) d++
    return d
  }
  const ordered = [...levels.entries()].sort(([a], [b]) => depth(b) - depth(a))

  const result = new Map<string, LayoutPosition>()
  const sizes = new Map<string, { width: number; height: number }>()
  for (const [parentId, members] of ordered) {
    const level = members.map((el) => (expanded.has(el.id) && sizes.has(el.id) ? { ...el, ...sizes.get(el.id) } : el))
    const { positions, width, height } = await layoutLevel(level, relationships, view, elements)
    const dx = parentId === null ? 0 : BOUNDARY_INSET.x
    const dy = parentId === null ? 0 : BOUNDARY_INSET.y
    for (const p of positions) result.set(p.elementId, { ...p, x: p.x + dx, y: p.y + dy })
    if (parentId !== null) {
      sizes.set(parentId, {
        width: Math.round(width + BOUNDARY_INSET.x + BOUNDARY_INSET.right),
        height: Math.round(height + BOUNDARY_INSET.y + BOUNDARY_INSET.bottom),
      })
    }
  }
  for (const [id, size] of sizes) {
    const p = result.get(id)
    if (p) result.set(id, { ...p, ...size })
  }
  return [...result.values()]
}
