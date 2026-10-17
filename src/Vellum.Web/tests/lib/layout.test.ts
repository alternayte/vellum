import { describe, it, expect } from 'vitest'
import { computeLayout, placeNew, BOUNDARY_INSET } from '../../src/lib/layout'

describe('computeLayout', () => {
  it('positions two connected nodes with adequate spacing', async () => {
    const elements = [
      { id: 'a', width: 280, height: 120 },
      { id: 'b', width: 280, height: 120 },
    ]
    const relationships = [
      { id: 'r1', fromId: 'a', toId: 'b', label: 'reads and writes' },
    ]

    const { positions, routes } = await computeLayout(elements, relationships)

    expect(positions).toHaveLength(2)
    const posA = positions.find((p) => p.elementId === 'a')!
    const posB = positions.find((p) => p.elementId === 'b')!

    // The two cards are in separate layers, across or down: the gap holds the relationship label.
    const gapX = Math.abs(posB.x - posA.x) - 280
    const gapY = Math.abs(posB.y - posA.y) - 120
    expect(Math.max(gapX, gapY)).toBeGreaterThanOrEqual(gapX > gapY ? 'reads and writes'.length * 6.8 : 26)
    // The route runs from the box of one card to the box of the other, its label between them.
    const route = routes.find((r) => r.relationshipId === 'r1')!.route
    expect(route.from).toEqual({ x: posA.x, y: posA.y, width: 280, height: 120 })
    expect(route.to).toEqual({ x: posB.x, y: posB.y, width: 280, height: 120 })
    expect(route.label).not.toBeNull()
  })

  it('handles elements with no relationships', async () => {
    const elements = [
      { id: 'a' },
      { id: 'b' },
    ]
    const { positions } = await computeLayout(elements, [])

    expect(positions).toHaveLength(2)
  })

  it('returns a position for every input element', async () => {
    const elements = [
      { id: 'a' },
      { id: 'b' },
      { id: 'c' },
    ]
    const relationships = [
      { id: 'r1', fromId: 'a', toId: 'b' },
      { id: 'r2', fromId: 'b', toId: 'c' },
    ]

    const { positions } = await computeLayout(elements, relationships)
    expect(positions).toHaveLength(3)
    const ids = positions.map((p) => p.elementId).sort()
    expect(ids).toEqual(['a', 'b', 'c'])
  })
})

const chain = ['a', 'b', 'c', 'd'].map((id) => ({ id, parentId: null, width: 260, height: 120 }))
const chainLinks = [
  { id: 'ab', fromId: 'a', toId: 'b' },
  { id: 'bc', fromId: 'b', toId: 'c' },
  { id: 'cd', fromId: 'c', toId: 'd' },
]

function bounds(positions: { x: number; y: number }[]) {
  const xs = positions.map((p) => p.x)
  const ys = positions.map((p) => p.y)
  return { width: Math.max(...xs) - Math.min(...xs) + 260, height: Math.max(...ys) - Math.min(...ys) + 120 }
}

describe('computeLayout by level and canvas shape', () => {
  it('lays a chain across a wide canvas, not down a narrow column', async () => {
    const box = bounds((await computeLayout(chain, chainLinks, { width: 1392, height: 780 })).positions)
    expect(box.width).toBeGreaterThan(box.height)
  })

  it('lays a chain down a tall canvas', async () => {
    const box = bounds((await computeLayout(chain, chainLinks, { width: 390, height: 700 })).positions)
    expect(box.height).toBeGreaterThan(box.width)
  })

  it('lays out each level on its own and sizes the parent to hold its children', async () => {
    const elements = [
      { id: 'sys', parentId: null, width: 288, height: 120 },
      { id: 'other', parentId: null, width: 288, height: 120 },
      { id: 'api', parentId: 'sys', width: 264, height: 120 },
      { id: 'db', parentId: 'sys', width: 264, height: 120 },
    ]
    const { positions } = await computeLayout(elements, [{ id: 'r', fromId: 'api', toId: 'db' }], { width: 1392, height: 780 })
    const byId = new Map(positions.map((p) => [p.elementId, p]))

    // The children start inside the boundary, below its title, whatever the top level does.
    const api = byId.get('api')!
    const db = byId.get('db')!
    expect(Math.min(api.x, db.x)).toBe(BOUNDARY_INSET.x)
    expect(Math.min(api.y, db.y)).toBe(BOUNDARY_INSET.y)

    const sys = byId.get('sys')!
    expect(sys.width).toBeGreaterThanOrEqual(Math.max(api.x, db.x) + 264 + BOUNDARY_INSET.right)
    expect(sys.height).toBeGreaterThanOrEqual(Math.max(api.y, db.y) + 120 + BOUNDARY_INSET.bottom)
    expect(byId.get('other')!.width).toBeUndefined()
  })

  it('makes room on its level for an element shown open', async () => {
    const elements = [
      { id: 'sys', parentId: null, width: 288, height: 120 },
      { id: 'other', parentId: null, width: 288, height: 120 },
      ...['a', 'b', 'c', 'd'].map((id) => ({ id, parentId: 'sys', width: 264, height: 120 })),
    ]
    const links = [{ id: 'r', fromId: 'sys', toId: 'other' }, { id: 'ab', fromId: 'a', toId: 'b' }, { id: 'cd', fromId: 'c', toId: 'd' }]
    const { positions } = await computeLayout(elements, links, { width: 1392, height: 780 }, new Set(['sys']))
    const byId = new Map(positions.map((p) => [p.elementId, p]))
    const sys = byId.get('sys')!
    const other = byId.get('other')!
    const apart = other.x >= sys.x + sys.width! || other.y >= sys.y + sys.height! || sys.x >= other.x + 288 || sys.y >= other.y + 120
    expect(apart).toBe(true)
  })
})

describe('placeNew', () => {
  // The orders fixture after a rescan adds the store payments: the system is open on the
  // canvas, and its saved size holds the four cards a person sees.
  const orders = [
    { id: 'orders', parentId: null, width: 288, height: 128 },
    { id: 'api', parentId: 'orders', width: 264, height: 128 },
    { id: 'worker', parentId: 'orders', width: 264, height: 128 },
    { id: 'billing', parentId: 'orders', width: 264, height: 128 },
    { id: 'postgres', parentId: 'orders', width: 264, height: 128 },
    { id: 'payments', parentId: 'orders', width: 264, height: 128 },
  ]
  const saved = [
    { elementId: 'orders', x: 0, y: 0, width: 1224, height: 474 },
    { elementId: 'api', x: 40, y: 69 },
    { elementId: 'worker', x: 480, y: 106 },
    { elementId: 'billing', x: 480, y: 306 },
    { elementId: 'postgres', x: 920, y: 64 },
  ]
  const links = [
    { id: 'r1', fromId: 'api', toId: 'postgres' },
    { id: 'r2', fromId: 'worker', toId: 'postgres' },
    { id: 'r3', fromId: 'payments', toId: 'postgres' },
  ]
  type Box = { x: number; y: number; width: number; height: number }
  const overlaps = (a: Box, b: Box) => a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height

  it('puts a new card in free space inside the boundary of its open parent', async () => {
    const { positions: placed } = await placeNew(orders, links, saved, { width: 1248, height: 820 }, new Set(['orders']))
    const payments = placed.find((p) => p.elementId === 'payments')!
    const box = { x: payments.x, y: payments.y, width: 264, height: 128 }
    for (const kept of saved.filter((p) => p.elementId !== 'orders')) {
      expect(overlaps(box, { x: kept.x, y: kept.y, width: 264, height: 128 })).toBe(false)
    }
    // The canvas keeps a child inside its parent, so the parent grows to hold the new card.
    const parent = placed.find((p) => p.elementId === 'orders')!
    expect(parent.x).toBe(0)
    expect(parent.y).toBe(0)
    expect(parent.width!).toBeGreaterThanOrEqual(box.x + box.width + BOUNDARY_INSET.right)
    expect(parent.height!).toBeGreaterThanOrEqual(box.y + box.height + BOUNDARY_INSET.bottom)
  })
})
