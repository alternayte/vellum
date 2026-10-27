import ELK, { type ElkExtendedEdge, type ElkNode } from 'elkjs/lib/elk.bundled.js'
import { derivedLineLabel } from '@/lib/derived-lines'
import { ALL_LINES_SHOWN, applyLineVisibility, type LineVisibility } from '@/lib/line-visibility'
import type { LayoutRoute, Rect, RoutePoint } from '@/lib/edge-utils'

const elk = new ELK()

export interface LayoutElement {
  id: string
  parentId?: string | null
  width?: number
  height?: number
}

export interface LayoutRelationship {
  id: string
  fromId: string
  toId: string
  label?: string | null
  technology?: string | null
  lineShape?: string | null
  /** `structural` or `behavioural`: a view can hide each kind. */
  kind?: string | null
}

export interface LayoutPosition {
  elementId: string
  x: number
  y: number
  width?: number
  height?: number
}

/**
 * The route of one line the layout drew: a relationship between two cards of a level
 * (`relationshipId`), or a derived line between two cards of a level (`relationshipId` null).
 */
export interface LineRoute {
  relationshipId: string | null
  fromId: string
  toId: string
  route: LayoutRoute
}

export interface Layout {
  positions: LayoutPosition[]
  routes: LineRoute[]
}

/**
 * Where the children of an element start inside its boundary, and the margin the boundary
 * keeps around them. An expanded element draws its children at their saved positions relative
 * to its own corner, so a level inside an element starts below the boundary's title.
 */
export const BOUNDARY_INSET = { x: 40, y: 64, right: 40, bottom: 40 }

const DEFAULT_SIZE = { width: 264, height: 120 }

// Each level is laid out in both directions. The one that shows larger on the visible canvas
// wins: a chain of four on a wide screen goes across, not down a narrow column. A label has its
// own place between two layers, so the gap between layers is the room for the lines.
const STRAIGHT: Record<string, string>[] = [
  { 'elk.direction': 'DOWN', 'elk.layered.spacing.nodeNodeBetweenLayers': '64' },
  { 'elk.direction': 'RIGHT', 'elk.layered.spacing.nodeNodeBetweenLayers': '72' },
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

// Orthogonal routing gives each line a path through the gaps between cards: a line between two
// cards that are layers apart passes the cards between them instead of crossing them. A label
// sits in the middle of its line, in a place the layout keeps free.
const BASE_OPTIONS: Record<string, string> = {
  'elk.algorithm': 'layered',
  'elk.spacing.nodeNode': '72',
  'elk.spacing.componentComponent': '96',
  'elk.spacing.edgeNode': '24',
  'elk.spacing.edgeEdge': '16',
  'elk.spacing.edgeLabel': '6',
  'elk.layered.spacing.edgeNodeBetweenLayers': '24',
  'elk.layered.spacing.edgeEdgeBetweenLayers': '14',
  'elk.edgeRouting': 'ORTHOGONAL',
  'elk.edgeLabels.placement': 'CENTER',
  'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
  'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
}

// The size of a label on the canvas (EdgeLabelCard and the derived line badge) and in the SVG
// export, a little wide: text is not measured here. The layout keeps this room free for it.
const LABEL_CHAR = 6.8
const TECHNOLOGY_CHAR = 6.2

export function relationshipLabelSize(r: LayoutRelationship): { width: number; height: number } | null {
  const label = r.label?.trim() ?? ''
  const technology = r.technology?.trim() ?? ''
  if (!label && !technology) return null
  return {
    width: Math.ceil(Math.max(label.length * LABEL_CHAR, technology.length * TECHNOLOGY_CHAR) + 22),
    height: 10 + (label ? 16 : 0) + (technology ? 15 : 0),
  }
}

export function derivedLabelSize(count: number, label: string | null): { width: number; height: number } {
  const badge = Math.max(16, String(count).length * 6.6 + 8)
  return { width: Math.ceil(6 + badge + (label ? 6 + label.length * LABEL_CHAR : 0) + 6), height: 24 }
}

/** The zoom at which a fit shows a box of this size on the viewport, at most the fit's limit. */
function fitZoom(width: number, height: number, viewport: Viewport): number {
  const zoom = Math.min((viewport.width - 2 * FIT_MARGIN) / Math.max(width, 1), (viewport.height - 2 * FIT_MARGIN) / Math.max(height, 1))
  return Math.min(MAX_ZOOM, zoom)
}

const usableViewport = (viewport: Viewport): Viewport =>
  viewport.width > 2 * FIT_MARGIN + 100 && viewport.height > 2 * FIT_MARGIN + 100 ? viewport : { width: 1392, height: 820 }

/**
 * The points of a route on whole pixels without the steps rounding makes: a run that moves a
 * pixel or less across takes the line of the run before it, and a point on a straight run
 * between two others goes.
 */
function tidyPoints(points: RoutePoint[]): RoutePoint[] {
  const snapped = points.map((p) => ({ ...p }))
  for (let i = 1; i < snapped.length; i++) {
    const a = snapped[i - 1]
    const b = snapped[i]
    if (Math.abs(b.y - a.y) <= 1) b.y = a.y
    if (Math.abs(b.x - a.x) <= 1) b.x = a.x
  }
  const kept = snapped.filter((p, i) => i === 0 || p.x !== snapped[i - 1].x || p.y !== snapped[i - 1].y)
  return kept.filter((p, i) => {
    if (i === 0 || i === kept.length - 1) return true
    const a = kept[i - 1]
    const c = kept[i + 1]
    return !((a.x === p.x && p.x === c.x) || (a.y === p.y && p.y === c.y))
  })
}

interface LevelLayout {
  positions: LayoutPosition[]
  routes: LineRoute[]
  width: number
  height: number
}

async function layoutLevel(
  level: LayoutElement[],
  relationships: LayoutRelationship[],
  viewport: Viewport,
  all: LayoutElement[],
  visibility: LineVisibility,
): Promise<LevelLayout> {
  const ids = new Set(level.map((e) => e.id))
  // A level is shaped by the lines the canvas draws on it with every card closed: the
  // relationships between its elements and the derived lines of those between their insides,
  // less the lines the view hides.
  const lines = applyLineVisibility(all.map((e) => ({ id: e.id, parentId: e.parentId ?? null })), relationships, ids, visibility)
  const byLine = new Map<string, { relationshipId: string | null; fromId: string; toId: string; label: { width: number; height: number } | null; routed: boolean }>()
  for (const r of lines.direct) {
    if (r.fromId === r.toId) continue
    byLine.set(r.id, { relationshipId: r.id, fromId: r.fromId, toId: r.toId, label: relationshipLabelSize(r), routed: r.lineShape !== 'straight' })
  }
  for (const d of lines.derived) {
    const members = relationships.filter((r) => d.relationshipIds.includes(r.id))
    byLine.set(d.id, { relationshipId: null, fromId: d.fromId, toId: d.toId, label: derivedLabelSize(d.relationshipIds.length, derivedLineLabel(members)), routed: true })
  }
  const edges: ElkExtendedEdge[] = [...byLine.entries()].map(([id, line]) => ({
    id,
    sources: [line.fromId],
    targets: [line.toId],
    // An inline label sits on its line, so it reads as the label of that line and of no other.
    ...(line.label ? { labels: [{ id: `${id}#label`, text: id, ...line.label, layoutOptions: { 'elk.edgeLabels.inline': 'true' } }] } : {}),
  }))
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
        edges: edges.map((e) => ({ ...e, labels: e.labels?.map((l) => ({ ...l, layoutOptions: { ...l.layoutOptions } })) })),
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
  // The level starts at the top left of what it draws: its cards, the lines between them and
  // their labels. A line may run above or beside the cards.
  const paths = new Map<string, { points: RoutePoint[]; label: Rect | null }>()
  for (const edge of (node.edges ?? []) as ElkExtendedEdge[]) {
    const sections = edge.sections ?? []
    if (sections.length !== 1) continue
    const s = sections[0]
    const points = [s.startPoint, ...(s.bendPoints ?? []), s.endPoint].map((p) => ({ x: p.x, y: p.y }))
    const l = edge.labels?.[0]
    const label = l && l.x !== undefined && l.y !== undefined ? { x: l.x, y: l.y, width: l.width ?? 0, height: l.height ?? 0 } : null
    paths.set(edge.id, { points, label })
  }
  const xs: number[] = []
  const ys: number[] = []
  const xe: number[] = []
  const ye: number[] = []
  for (const c of placed) {
    xs.push(c.x ?? 0); ys.push(c.y ?? 0); xe.push((c.x ?? 0) + (c.width ?? 0)); ye.push((c.y ?? 0) + (c.height ?? 0))
  }
  for (const p of paths.values()) {
    for (const q of p.points) { xs.push(q.x); ys.push(q.y); xe.push(q.x); ye.push(q.y) }
    if (p.label) { xs.push(p.label.x); ys.push(p.label.y); xe.push(p.label.x + p.label.width); ye.push(p.label.y + p.label.height) }
  }
  const minX = Math.min(...xs)
  const minY = Math.min(...ys)
  const at = (v: number, min: number) => Math.round(v - min)

  const boxes = new Map<string, Rect>()
  const positions = placed.map((c) => {
    const x = at(c.x ?? 0, minX)
    const y = at(c.y ?? 0, minY)
    boxes.set(c.id, { x, y, width: c.width ?? 0, height: c.height ?? 0 })
    return { elementId: c.id, x, y }
  })
  const routes: LineRoute[] = []
  for (const [id, line] of byLine) {
    const path = paths.get(id)
    const from = boxes.get(line.fromId)
    const to = boxes.get(line.toId)
    if (!line.routed || !path || !from || !to) continue
    const points = tidyPoints(path.points.map((p) => ({ x: at(p.x, minX), y: at(p.y, minY) })))
    if (points.length < 2) continue
    routes.push({
      relationshipId: line.relationshipId,
      fromId: line.fromId,
      toId: line.toId,
      route: {
        from,
        to,
        points,
        label: path.label ? { x: at(path.label.x + path.label.width / 2, minX), y: at(path.label.y + path.label.height / 2, minY) } : null,
      },
    })
  }
  return { positions, routes, width: Math.max(...xe) - minX, height: Math.max(...ye) - minY }
}

const moveRoute = (r: LineRoute, dx: number, dy: number): LineRoute => ({
  ...r,
  route: {
    from: { ...r.route.from, x: r.route.from.x + dx, y: r.route.from.y + dy },
    to: { ...r.route.to, x: r.route.to.x + dx, y: r.route.to.y + dy },
    points: r.route.points.map((p) => ({ x: p.x + dx, y: p.y + dy })),
    label: r.route.label ? { x: r.route.label.x + dx, y: r.route.label.y + dy } : null,
  },
})

/** The gap between what a person placed on a level and the elements placed next to it. */
const PLACE_GAP = 120

interface Box {
  x: number
  y: number
  width: number
  height: number
}

const overlaps = (a: Box, b: Box) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height

function hull(boxes: Box[]): Box {
  const minX = Math.min(...boxes.map((b) => b.x))
  const minY = Math.min(...boxes.map((b) => b.y))
  return {
    x: minX,
    y: minY,
    width: Math.max(...boxes.map((b) => b.x + b.width)) - minX,
    height: Math.max(...boxes.map((b) => b.y + b.height)) - minY,
  }
}

/** The boxes a saved route takes on its level: its points and its label. */
function routeBoxes(route: LayoutRoute): Box[] {
  const p = hull(route.points.map((q) => ({ x: q.x, y: q.y, width: 0, height: 0 })))
  return route.label ? [p, { x: route.label.x - 40, y: route.label.y - 12, width: 80, height: 24 }] : [p]
}

/**
 * Places the elements that have no saved position, with the same layout as `computeLayout`, and
 * moves nothing that has one. A view with no saved position is laid out whole, with its routes.
 * On a level that has saved positions, the new elements of the level are laid out as one group
 * in free space: beside or below what is there (the cards and the saved routes of the level),
 * on the side that shows the level larger on the viewport. A parent whose boundary is saved
 * grows to hold its children, since the canvas keeps a child inside its open parent.
 *
 * Returns the positions of the new elements, and of each parent that grows. `routes` is the
 * routes of a whole layout, or null when the saved routes stay.
 */
export async function placeNew(
  elements: LayoutElement[],
  relationships: LayoutRelationship[],
  saved: LayoutPosition[],
  viewport: Viewport = { width: 1392, height: 820 },
  expanded: ReadonlySet<string> = new Set(),
  savedRoutes: { fromId: string; route: LayoutRoute }[] = [],
  visibility: LineVisibility = ALL_LINES_SHOWN,
): Promise<{ positions: LayoutPosition[]; routes: LineRoute[] | null }> {
  const placed = new Map(saved.map((p) => [p.elementId, p]))
  const fresh = elements.filter((e) => !placed.has(e.id))
  if (fresh.length === 0) return { positions: [], routes: null }
  const full = await computeLayout(elements, relationships, viewport, expanded, visibility)
  if (placed.size === 0) return full

  const byId = new Map(full.positions.map((p) => [p.elementId, p]))
  const elementById = new Map(elements.map((e) => [e.id, e]))
  const levelOf = (id: string) => elementById.get(id)?.parentId ?? null
  const view = usableViewport(viewport)
  const out = new Map<string, LayoutPosition>()
  const boxOf = (id: string): Box | null => {
    const p = out.get(id) ?? placed.get(id)
    if (!p) return null
    const el = elementById.get(id)
    return { x: p.x, y: p.y, width: p.width ?? el?.width ?? DEFAULT_SIZE.width, height: p.height ?? el?.height ?? DEFAULT_SIZE.height }
  }
  // The saved routes of a level, of lines whose cards are on it.
  const routesOn = (parentId: string | null) =>
    savedRoutes.filter((r) => placed.has(r.fromId) && elementById.has(r.fromId) && levelOf(r.fromId) === parentId).flatMap((r) => routeBoxes(r.route))
  const siblingsOf = (parentId: string | null, except: string) =>
    elements.filter((e) => levelOf(e.id) === parentId && e.id !== except).map((e) => boxOf(e.id)).filter((b): b is Box => b !== null)

  // A parent whose boundary is saved grows to hold its children and the routes between them.
  // Its own parent grows in turn.
  const grow = (parentId: string | null) => {
    for (let id = parentId; id !== null; id = levelOf(id)) {
      const p = out.get(id) ?? placed.get(id)
      if (!p?.width || !p.height) return
      const inside = [
        ...elements.filter((e) => levelOf(e.id) === id).map((e) => boxOf(e.id)).filter((b): b is Box => b !== null),
        ...routesOn(id),
      ]
      if (inside.length === 0) return
      const h = hull(inside)
      const width = Math.max(p.width, Math.round(h.x + h.width + BOUNDARY_INSET.right))
      const height = Math.max(p.height, Math.round(h.y + h.height + BOUNDARY_INSET.bottom))
      if (width === p.width && height === p.height) return
      out.set(id, { ...p, width, height })
    }
  }

  const levels = new Map<string | null, LayoutElement[]>()
  for (const el of fresh) levels.set(levelOf(el.id), [...(levels.get(levelOf(el.id)) ?? []), el])
  // The deepest levels first: a parent placed new on its own level takes the size its inside needs.
  const depth = (id: string | null) => {
    let d = 0
    for (let at = id; at !== null && d <= elements.length; at = levelOf(at)) d++
    return d
  }
  for (const [parentId, members] of [...levels.entries()].sort(([a], [b]) => depth(b) - depth(a))) {
    const taken = [
      ...elements.filter((e) => levelOf(e.id) === parentId && placed.has(e.id)).map((e) => boxOf(e.id)!),
      ...routesOn(parentId),
    ]
    if (taken.length === 0) {
      // Nothing on the level is placed: the whole layout's places, inside the parent's boundary.
      for (const el of members) out.set(el.id, byId.get(el.id)!)
      grow(parentId)
      continue
    }
    const sized = members.map((el) => {
      const size = out.get(el.id) ?? byId.get(el.id)
      return expanded.has(el.id) && size?.width && size.height ? { ...el, width: size.width, height: size.height } : el
    })
    const group = await layoutLevel(sized, relationships, view, elements, visibility)
    const there = hull(taken)
    const candidates = [
      { x: there.x + there.width + PLACE_GAP, y: there.y },
      { x: there.x, y: there.y + there.height + PLACE_GAP },
    ]
    // The side whose grown parent covers no card beside the parent, then the side that shows the
    // level larger on the viewport.
    const parentBox = parentId === null ? null : boxOf(parentId)
    const parentSiblings = parentId === null ? [] : siblingsOf(levelOf(parentId), parentId)
    const scored = candidates.map((at) => {
      const box = { x: at.x, y: at.y, width: group.width, height: group.height }
      const all = hull([there, box])
      let clear = true
      if (parentBox) {
        const grown = {
          ...parentBox,
          width: Math.max(parentBox.width, all.x + all.width + BOUNDARY_INSET.right),
          height: Math.max(parentBox.height, all.y + all.height + BOUNDARY_INSET.bottom),
        }
        clear = !parentSiblings.some((s) => overlaps(grown, s))
      }
      return { at, clear, zoom: fitZoom(all.width, all.height, view) }
    })
    const chosen = scored.reduce((a, b) => (b.clear && !a.clear) || (b.clear === a.clear && b.zoom > a.zoom * 1.05) ? b : a)
    for (const p of group.positions) {
      const size = out.get(p.elementId) ?? byId.get(p.elementId)
      out.set(p.elementId, { ...p, x: p.x + Math.round(chosen.at.x), y: p.y + Math.round(chosen.at.y), width: size?.width, height: size?.height })
    }
    grow(parentId)
  }
  return { positions: [...out.values()], routes: null }
}

/**
 * Lays out every level of the model on its own: the top level, and the children of each
 * element. The relationships between siblings and the derived lines between them shape a level,
 * since the canvas draws a level with only its own elements. `viewport` is the size of the visible canvas in screen pixels.
 *
 * Children are placed from `BOUNDARY_INSET`, and their parent gets the size that holds them and
 * the lines between them, so an expanded parent draws its children inside its boundary. An
 * element in `expanded` is shown open on its level, so its level makes room for that size, not
 * for its card.
 *
 * Each line of a level gets the route the layout drew for it, in the coordinates of its level:
 * the relationships between two cards of the level (except a straight line) and its derived lines.
 * A line the view hides (`visibility`) does not shape its level and gets no route.
 */
export async function computeLayout(
  elements: LayoutElement[],
  relationships: LayoutRelationship[],
  viewport: Viewport = { width: 1392, height: 820 },
  expanded: ReadonlySet<string> = new Set(),
  visibility: LineVisibility = ALL_LINES_SHOWN,
): Promise<Layout> {
  const levels = new Map<string | null, LayoutElement[]>()
  for (const el of elements) {
    const key = el.parentId ?? null
    levels.set(key, [...(levels.get(key) ?? []), el])
  }
  const view = usableViewport(viewport)

  // The deepest levels first: an open element needs the size of its inside before its own level.
  const parentOf = new Map(elements.map((e) => [e.id, e.parentId ?? null]))
  const depth = (id: string | null) => {
    let d = 0
    for (let at = id; at !== null && d <= elements.length; at = parentOf.get(at) ?? null) d++
    return d
  }
  const ordered = [...levels.entries()].sort(([a], [b]) => depth(b) - depth(a))

  const result = new Map<string, LayoutPosition>()
  const routes: LineRoute[] = []
  const sizes = new Map<string, { width: number; height: number }>()
  for (const [parentId, members] of ordered) {
    const level = members.map((el) => (expanded.has(el.id) && sizes.has(el.id) ? { ...el, ...sizes.get(el.id) } : el))
    const laid = await layoutLevel(level, relationships, view, elements, visibility)
    const dx = parentId === null ? 0 : BOUNDARY_INSET.x
    const dy = parentId === null ? 0 : BOUNDARY_INSET.y
    for (const p of laid.positions) result.set(p.elementId, { ...p, x: p.x + dx, y: p.y + dy })
    for (const r of laid.routes) routes.push(moveRoute(r, dx, dy))
    if (parentId !== null) {
      sizes.set(parentId, {
        width: Math.round(laid.width + BOUNDARY_INSET.x + BOUNDARY_INSET.right),
        height: Math.round(laid.height + BOUNDARY_INSET.y + BOUNDARY_INSET.bottom),
      })
    }
  }
  for (const [id, size] of sizes) {
    const p = result.get(id)
    if (p) result.set(id, { ...p, ...size })
  }
  // A route keeps the box each end had on its level: the card, or the boundary of an element
  // shown open. A change of what is open changes the box, and the line falls back.
  return { positions: [...result.values()], routes }
}
