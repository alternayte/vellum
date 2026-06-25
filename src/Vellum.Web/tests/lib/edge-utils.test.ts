import { describe, it, expect } from 'vitest'
import { computeEdgeOffset, computeLabelOffset } from '../../src/lib/edge-utils'

describe('computeEdgeOffset', () => {
  it('returns zero offset for a single edge', () => {
    const result = computeEdgeOffset(0, 1)
    expect(result).toEqual({ sourceOffset: 0, targetOffset: 0 })
  })

  it('offsets two parallel edges symmetrically', () => {
    const first = computeEdgeOffset(0, 2)
    const second = computeEdgeOffset(1, 2)
    expect(first.sourceOffset).toBe(-15)
    expect(second.sourceOffset).toBe(15)
    expect(first.sourceOffset).toBe(-second.sourceOffset)
  })

  it('offsets three parallel edges with center at zero', () => {
    const a = computeEdgeOffset(0, 3)
    const b = computeEdgeOffset(1, 3)
    const c = computeEdgeOffset(2, 3)
    expect(b.sourceOffset).toBe(0)
    expect(a.sourceOffset).toBe(-30)
    expect(c.sourceOffset).toBe(30)
  })
})

describe('computeLabelOffset', () => {
  it('returns 0.5 for a single edge', () => {
    expect(computeLabelOffset(0, 1)).toBe(0.5)
  })

  it('spreads labels for two parallel edges', () => {
    const a = computeLabelOffset(0, 2)
    const b = computeLabelOffset(1, 2)
    expect(a).toBeCloseTo(0.4)
    expect(b).toBeCloseTo(0.6)
    expect(a + b).toBeCloseTo(1.0)
  })

  it('clamps label position between 0.2 and 0.8', () => {
    const positions = Array.from({ length: 7 }, (_, i) => computeLabelOffset(i, 7))
    for (const p of positions) {
      expect(p).toBeGreaterThanOrEqual(0.2)
      expect(p).toBeLessThanOrEqual(0.8)
    }
  })
})
