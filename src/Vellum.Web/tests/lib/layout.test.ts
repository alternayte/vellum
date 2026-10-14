import { describe, it, expect } from 'vitest'
import { computeLayout, BOUNDARY_INSET } from '../../src/lib/layout'

describe('computeLayout', () => {
  it('positions two connected nodes with adequate spacing', async () => {
    const elements = [
      { id: 'a', width: 280, height: 120 },
      { id: 'b', width: 280, height: 120 },
    ]
    const relationships = [
      { id: 'r1', fromId: 'a', toId: 'b' },
    ]

    const positions = await computeLayout(elements, relationships)

    expect(positions).toHaveLength(2)
    const posA = positions.find((p) => p.elementId === 'a')!
    const posB = positions.find((p) => p.elementId === 'b')!

    // The two cards are in separate layers, across or down: the gap holds the relationship label.
    const gapX = Math.abs(posB.x - posA.x) - 280
    const gapY = Math.abs(posB.y - posA.y) - 120
    expect(Math.max(gapX, gapY)).toBeGreaterThanOrEqual(120)
  })

  it('handles elements with no relationships', async () => {
    const elements = [
      { id: 'a' },
      { id: 'b' },
    ]
    const positions = await computeLayout(elements, [])

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

    const positions = await computeLayout(elements, relationships)
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
    const box = bounds(await computeLayout(chain, chainLinks, { width: 1392, height: 780 }))
    expect(box.width).toBeGreaterThan(box.height)
  })

  it('lays a chain down a tall canvas', async () => {
    const box = bounds(await computeLayout(chain, chainLinks, { width: 390, height: 700 }))
    expect(box.height).toBeGreaterThan(box.width)
  })

  it('lays out each level on its own and sizes the parent to hold its children', async () => {
    const elements = [
      { id: 'sys', parentId: null, width: 288, height: 120 },
      { id: 'other', parentId: null, width: 288, height: 120 },
      { id: 'api', parentId: 'sys', width: 264, height: 120 },
      { id: 'db', parentId: 'sys', width: 264, height: 120 },
    ]
    const positions = await computeLayout(elements, [{ id: 'r', fromId: 'api', toId: 'db' }], { width: 1392, height: 780 })
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
    const positions = await computeLayout(elements, links, { width: 1392, height: 780 }, new Set(['sys']))
    const byId = new Map(positions.map((p) => [p.elementId, p]))
    const sys = byId.get('sys')!
    const other = byId.get('other')!
    const apart = other.x >= sys.x + sys.width! || other.y >= sys.y + sys.height! || sys.x >= other.x + 288 || sys.y >= other.y + 120
    expect(apart).toBe(true)
  })
})
