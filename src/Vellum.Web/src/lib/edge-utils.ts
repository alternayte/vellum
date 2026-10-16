import { getBezierPath, getStraightPath, getSmoothStepPath, Position } from '@xyflow/react'

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

/**
 * Attach an edge to the sides of two nodes that face each other, so the path and its label
 * stay in the gap between them. `offset` slides both ends along their side to separate
 * parallel edges.
 */
export function facingEndpoints(source: Rect, target: Rect, offset = 0): EdgeEndpoints {
  return relationshipEnds(source, target, offset)
}
