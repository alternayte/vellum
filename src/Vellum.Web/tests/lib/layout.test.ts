import { describe, it, expect } from 'vitest'
import { computeLayout } from '../../src/lib/layout'

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

    // With top-down layout, nodes should be separated vertically
    // At least 120px (node height) + 160px (layer spacing) apart
    const verticalGap = Math.abs(posB.y - posA.y)
    expect(verticalGap).toBeGreaterThanOrEqual(280)
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
