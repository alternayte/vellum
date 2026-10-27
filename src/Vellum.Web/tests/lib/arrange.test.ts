import { describe, expect, it } from 'vitest'
import { align, distribute, type Box } from '@/lib/arrange'

const boxes: Box[] = [
  { id: 'a', x: 0, y: 10, width: 100, height: 50 },
  { id: 'b', x: 300, y: 100, width: 200, height: 80 },
  { id: 'c', x: 120, y: 40, width: 60, height: 20 },
]

describe('align', () => {
  it('lines up left and right edges on the box around the cards', () => {
    expect(align(boxes, 'left')).toEqual([
      { id: 'b', x: 0, y: 100 },
      { id: 'c', x: 0, y: 40 },
    ])
    expect(align(boxes, 'right')).toEqual([
      { id: 'a', x: 400, y: 10 },
      { id: 'c', x: 440, y: 40 },
    ])
  })

  it('centres on the middle of the box around the cards', () => {
    // The box runs from 0 to 500 across and from 10 to 180 down.
    expect(align(boxes, 'center')).toEqual([
      { id: 'a', x: 200, y: 10 },
      { id: 'b', x: 150, y: 100 },
      { id: 'c', x: 220, y: 40 },
    ])
    expect(align(boxes, 'middle')).toEqual([
      { id: 'a', x: 0, y: 70 },
      { id: 'b', x: 300, y: 55 },
      { id: 'c', x: 120, y: 85 },
    ])
  })

  it('moves only across or only down', () => {
    for (const move of align(boxes, 'top')) expect(move.x).toBe(boxes.find((b) => b.id === move.id)!.x)
    expect(align(boxes, 'bottom')).toEqual([
      { id: 'a', x: 0, y: 130 },
      { id: 'c', x: 120, y: 160 },
    ])
  })

  it('leaves a single card where it is', () => {
    expect(align([boxes[0]], 'left')).toEqual([])
  })
})

describe('distribute', () => {
  it('keeps the first and last card and makes the gaps equal', () => {
    // Across: a ends at 100, c is 60 wide, b starts at 300. The free room is 140, so each gap is 70.
    expect(distribute(boxes, 'horizontal')).toEqual([{ id: 'c', x: 170, y: 40 }])
  })

  it('orders the cards by their place on the axis', () => {
    // Down: a (10, 50 high), c (40, 20 high), b (100). Free room 90 - 70 = 20, so each gap is 10.
    expect(distribute(boxes, 'vertical')).toEqual([{ id: 'c', x: 120, y: 70 }])
  })

  it('needs three cards', () => {
    expect(distribute(boxes.slice(0, 2), 'horizontal')).toEqual([])
  })
})
