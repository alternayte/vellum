/**
 * Align and distribute for a selection of cards. Each function takes the boxes of the cards in
 * canvas coordinates and gives the new top-left corner of each card that moves. A card that
 * stays where it is is left out, so a save writes only what changed.
 */

export interface Box {
  id: string
  x: number
  y: number
  width: number
  height: number
}

export type AlignEdge = 'left' | 'center' | 'right' | 'top' | 'middle' | 'bottom'
export type DistributeAxis = 'horizontal' | 'vertical'

export interface Move {
  id: string
  x: number
  y: number
}

const round = (n: number) => Math.round(n * 100) / 100

function changed(boxes: Box[], next: Map<string, { x: number; y: number }>): Move[] {
  const moves: Move[] = []
  for (const box of boxes) {
    const to = next.get(box.id)
    if (!to) continue
    const x = round(to.x)
    const y = round(to.y)
    if (x !== round(box.x) || y !== round(box.y)) moves.push({ id: box.id, x, y })
  }
  return moves
}

/**
 * Lines the cards up on one edge or centre line of the box around them: left, center and right
 * move the cards across, top, middle and bottom move them up or down.
 */
export function align(boxes: Box[], edge: AlignEdge): Move[] {
  if (boxes.length < 2) return []
  const left = Math.min(...boxes.map((b) => b.x))
  const right = Math.max(...boxes.map((b) => b.x + b.width))
  const top = Math.min(...boxes.map((b) => b.y))
  const bottom = Math.max(...boxes.map((b) => b.y + b.height))
  const next = new Map<string, { x: number; y: number }>()
  for (const b of boxes) {
    switch (edge) {
      case 'left': next.set(b.id, { x: left, y: b.y }); break
      case 'center': next.set(b.id, { x: (left + right) / 2 - b.width / 2, y: b.y }); break
      case 'right': next.set(b.id, { x: right - b.width, y: b.y }); break
      case 'top': next.set(b.id, { x: b.x, y: top }); break
      case 'middle': next.set(b.id, { x: b.x, y: (top + bottom) / 2 - b.height / 2 }); break
      case 'bottom': next.set(b.id, { x: b.x, y: bottom - b.height }); break
    }
  }
  return changed(boxes, next)
}

/**
 * Spaces the cards evenly along one axis: the first and the last card stay, and the gaps
 * between neighbouring cards become equal. The order is the order of the cards on that axis.
 * Fewer than three cards have nothing to space.
 */
export function distribute(boxes: Box[], axis: DistributeAxis): Move[] {
  if (boxes.length < 3) return []
  const across = axis === 'horizontal'
  const start = (b: Box) => (across ? b.x : b.y)
  const size = (b: Box) => (across ? b.width : b.height)
  const sorted = [...boxes].sort((a, b) => start(a) - start(b) || a.id.localeCompare(b.id))
  const first = sorted[0]
  const last = sorted[sorted.length - 1]
  const span = start(last) + size(last) - start(first)
  const gap = (span - sorted.reduce((sum, b) => sum + size(b), 0)) / (sorted.length - 1)
  const next = new Map<string, { x: number; y: number }>()
  let at = start(first)
  for (const b of sorted) {
    next.set(b.id, across ? { x: at, y: b.y } : { x: b.x, y: at })
    at += size(b) + gap
  }
  return changed(boxes, next)
}
