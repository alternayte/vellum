import { getBezierPath, getStraightPath, getSmoothStepPath, Position, type InternalNode } from '@xyflow/react'

type PathFn = typeof getBezierPath

export function getEdgePathFn(lineShape: string | null | undefined): PathFn {
  switch (lineShape) {
    case 'straight': return getStraightPath as unknown as PathFn
    case 'step': return getSmoothStepPath as unknown as PathFn
    default: return getBezierPath
  }
}

const OFFSET_PX = 30

export function computeEdgeOffset(
  edgeIndex: number,
  totalParallel: number,
): { sourceOffset: number; targetOffset: number } {
  if (totalParallel <= 1) return { sourceOffset: 0, targetOffset: 0 }
  const offset = (edgeIndex - (totalParallel - 1) / 2) * OFFSET_PX
  return { sourceOffset: offset, targetOffset: offset }
}

const LABEL_SPREAD = 0.2
const LABEL_MIN = 0.2
const LABEL_MAX = 0.8

export function computeLabelOffset(
  edgeIndex: number,
  totalParallel: number,
): number {
  if (totalParallel <= 1) return 0.5
  const raw = 0.5 + (edgeIndex - (totalParallel - 1) / 2) * LABEL_SPREAD
  return Math.max(LABEL_MIN, Math.min(LABEL_MAX, raw))
}

export interface Rect {
  x: number
  y: number
  width: number
  height: number
}

export interface EdgeEndpoints {
  sourceX: number
  sourceY: number
  sourcePosition: Position
  targetX: number
  targetY: number
  targetPosition: Position
}

function sidePoint(rect: Rect, side: Position, offset: number): { x: number; y: number } {
  const cx = rect.x + rect.width / 2
  const cy = rect.y + rect.height / 2
  switch (side) {
    case Position.Left: return { x: rect.x, y: cy + offset }
    case Position.Right: return { x: rect.x + rect.width, y: cy + offset }
    case Position.Top: return { x: cx + offset, y: rect.y }
    default: return { x: cx + offset, y: rect.y + rect.height }
  }
}

/** A side of a card where a relationship attaches. Each card has a handle with this ID on each side. */
export type CardSide = 'top' | 'right' | 'bottom' | 'left'

export const CARD_SIDES: readonly CardSide[] = ['top', 'right', 'bottom', 'left']

/** The saved sides of a relationship in a view. A null or missing end is auto. */
export interface RelationshipSides {
  sourceSide?: string | null
  targetSide?: string | null
}

const POSITION_OF: Record<CardSide, Position> = {
  top: Position.Top,
  right: Position.Right,
  bottom: Position.Bottom,
  left: Position.Left,
}

/** The side a saved word names, or null for auto and for a word that names no side. */
export function cardSide(word: string | null | undefined): CardSide | null {
  return word === 'top' || word === 'right' || word === 'bottom' || word === 'left' ? word : null
}

/**
 * The sides of two cards that face each other: across the larger gap between them, horizontal
 * on a tie. This is the auto side of each end, so it follows the cards as they move.
 */
export function facingSides(source: Rect, target: Rect): { source: CardSide; target: CardSide } {
  const dx = target.x + target.width / 2 - (source.x + source.width / 2)
  const dy = target.y + target.height / 2 - (source.y + source.height / 2)
  // Compare the gaps, not the centre distances: wide cards side by side must still
  // connect horizontally even when their centres are far apart vertically-speaking.
  const gapX = Math.abs(dx) - (source.width + target.width) / 2
  const gapY = Math.abs(dy) - (source.height + target.height) / 2
  if (gapX >= gapY) return dx >= 0 ? { source: 'right', target: 'left' } : { source: 'left', target: 'right' }
  return dy >= 0 ? { source: 'bottom', target: 'top' } : { source: 'top', target: 'bottom' }
}

/** The side each end attaches to: the saved side, or the facing side for an auto end. */
export function effectiveSides(source: Rect, target: Rect, saved?: RelationshipSides | null): { source: CardSide; target: CardSide } {
  const facing = facingSides(source, target)
  return {
    source: cardSide(saved?.sourceSide) ?? facing.source,
    target: cardSide(saved?.targetSide) ?? facing.target,
  }
}

/**
 * The ends of a relationship between two cards. A saved side stays; an auto end takes the side
 * that faces the other card. `offset` slides both ends along their side to separate parallel
 * edges. DiagramSvg.Attach on the server draws the SVG export by the same rule.
 */
export function relationshipEnds(source: Rect, target: Rect, offset = 0, saved?: RelationshipSides | null): EdgeEndpoints {
  const sides = effectiveSides(source, target, saved)
  const sourcePosition = POSITION_OF[sides.source]
  const targetPosition = POSITION_OF[sides.target]
  const s = sidePoint(source, sourcePosition, offset)
  const t = sidePoint(target, targetPosition, offset)
  return { sourceX: s.x, sourceY: s.y, sourcePosition, targetX: t.x, targetY: t.y, targetPosition }
}

export interface RoutePoint {
  x: number
  y: number
}

/**
 * The path a line takes in a view, from the auto layout (Tidy): the points from the source card
 * to the target card, in the coordinates of the level the two cards are on, and the centre of the
 * label the layout made room for. `from` and `to` are the boxes of the two cards when the layout
 * ran. The route holds only while both cards keep those boxes: a card that moves or changes size
 * makes the line fall back to its auto sides. DiagramSvg on the server draws a route by the same
 * rule (LayoutRoute.cs).
 */
export interface LayoutRoute {
  from: Rect
  to: Rect
  points: RoutePoint[]
  label: RoutePoint | null
}

/** The most points a route holds. A longer one is not a route of the auto layout. */
export const MAX_ROUTE_POINTS = 200

const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v)
const point = (v: unknown): RoutePoint | null =>
  v && typeof v === 'object' && finite((v as RoutePoint).x) && finite((v as RoutePoint).y) ? { x: (v as RoutePoint).x, y: (v as RoutePoint).y } : null
const box = (v: unknown): Rect | null => {
  const p = point(v)
  const r = v as Rect
  return p && finite(r.width) && finite(r.height) && r.width > 0 && r.height > 0 ? { ...p, width: r.width, height: r.height } : null
}

/** The route in a saved value, or null when the value is not a whole route. */
export function parseRoute(value: unknown): LayoutRoute | null {
  if (!value || typeof value !== 'object') return null
  const v = value as Record<string, unknown>
  const from = box(v.from)
  const to = box(v.to)
  if (!from || !to || !Array.isArray(v.points) || v.points.length < 2 || v.points.length > MAX_ROUTE_POINTS) return null
  const points = v.points.map(point)
  if (points.some((p) => p === null)) return null
  const label = v.label == null ? null : point(v.label)
  if (v.label != null && label === null) return null
  return { from, to, points: points as RoutePoint[], label }
}

/** How far a card may be from the box of a route, in pixels, and the route still holds. */
const ROUTE_TOLERANCE = 1

const sameBox = (a: Rect, b: Rect) =>
  Math.abs(a.x - b.x) <= ROUTE_TOLERANCE && Math.abs(a.y - b.y) <= ROUTE_TOLERANCE
  && Math.abs(a.width - b.width) <= ROUTE_TOLERANCE && Math.abs(a.height - b.height) <= ROUTE_TOLERANCE

/** True when the two cards have the boxes the route was laid out for, in level coordinates. */
export function routeFits(route: LayoutRoute, source: Rect, target: Rect): boolean {
  return sameBox(route.from, source) && sameBox(route.to, target)
}

/**
 * A saved route draws only for a relationship without a saved side and with a curved or stepped
 * line: a side or a straight line that a person chose wins over the layout.
 */
export function routeApplies(saved: RelationshipSides | null | undefined, lineShape: string | null | undefined): boolean {
  return !cardSide(saved?.sourceSide) && !cardSide(saved?.targetSide) && lineShape !== 'straight'
}

/** The radius of the corners of a drawn route. */
export const ROUTE_CORNER = 10

const fmt = (n: number) => {
  const r = Math.round(n * 100) / 100
  return Object.is(r, -0) ? '0' : String(r)
}

/**
 * The SVG path of a route moved by `dx`, `dy`: straight runs with rounded corners. A corner's
 * radius is at most half of each run next to it. DiagramSvg.RoutePath writes the same path.
 */
export function routePath(points: RoutePoint[], dx = 0, dy = 0): string {
  const at = points.map((p) => ({ x: p.x + dx, y: p.y + dy }))
  let d = `M${fmt(at[0].x)},${fmt(at[0].y)}`
  for (let i = 1; i < at.length - 1; i++) {
    const [a, b, c] = [at[i - 1], at[i], at[i + 1]]
    const before = Math.hypot(b.x - a.x, b.y - a.y)
    const after = Math.hypot(c.x - b.x, c.y - b.y)
    const r = Math.min(ROUTE_CORNER, before / 2, after / 2)
    if (r <= 0) {
      d += ` L${fmt(b.x)},${fmt(b.y)}`
      continue
    }
    const p1 = { x: b.x + ((a.x - b.x) / before) * r, y: b.y + ((a.y - b.y) / before) * r }
    const p2 = { x: b.x + ((c.x - b.x) / after) * r, y: b.y + ((c.y - b.y) / after) * r }
    d += ` L${fmt(p1.x)},${fmt(p1.y)} Q${fmt(b.x)},${fmt(b.y)} ${fmt(p2.x)},${fmt(p2.y)}`
  }
  const last = at[at.length - 1]
  return `${d} L${fmt(last.x)},${fmt(last.y)}`
}

/**
 * The box of a measured node on the canvas (`absolute`), and on its level (`level`): the place it
 * is saved at, relative to an open parent. `z` is the stacking order React Flow gives the node.
 * Null until React Flow has measured the node.
 */
export function nodeBoxes(node: InternalNode | undefined): { absolute: Rect; level: Rect; z: number } | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return {
    absolute: { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height },
    level: { x: node.position.x, y: node.position.y, width, height },
    z: node.internals.z,
  }
}

/**
 * The stacking order of the label of a line between two nodes. React Flow raises a line between
 * the cards inside an open parent to the order of those cards; its label rises with it, or the
 * line would cross the label's text.
 */
export function labelZ(source: { z: number } | null, target: { z: number } | null): number | undefined {
  const z = Math.max(source?.z ?? 0, target?.z ?? 0)
  return z > 0 ? z : undefined
}

/**
 * The path and the label point of a saved route on the canvas, or null when the route does not
 * hold for the two nodes now (a card moved or changed size): the line then falls back to its auto
 * sides. The route is in level coordinates, so it moves with an open parent.
 */
export function routeOnCanvas(
  route: LayoutRoute | null | undefined,
  source: { absolute: Rect; level: Rect } | null,
  target: { absolute: Rect; level: Rect } | null,
): { path: string; labelX: number; labelY: number } | null {
  if (!route || !source || !target || !routeFits(route, source.level, target.level)) return null
  const dx = source.absolute.x - source.level.x
  const dy = source.absolute.y - source.level.y
  const label = route.label ?? routeMiddle(route.points)
  return { path: routePath(route.points, dx, dy), labelX: label.x + dx, labelY: label.y + dy }
}

/** The middle of a route, for a label the layout gave no place. DiagramSvg.Middle takes the same point. */
export function routeMiddle(points: RoutePoint[]): RoutePoint {
  const p = points[Math.floor(points.length / 2)]
  const q = points[Math.floor((points.length - 1) / 2)]
  return { x: (p.x + q.x) / 2, y: (p.y + q.y) / 2 }
}

/**
 * Attach an edge to the sides of two nodes that face each other, so the path and its label
 * stay in the gap between them. `offset` slides both ends along their side to separate
 * parallel edges.
 */
export function facingEndpoints(source: Rect, target: Rect, offset = 0): EdgeEndpoints {
  return relationshipEnds(source, target, offset)
}
