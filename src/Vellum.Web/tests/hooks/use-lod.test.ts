import { describe, it, expect } from 'vitest'
import { useLod } from '../../src/hooks/use-lod'

describe('useLod', () => {
  it('returns full at zoom 1.0 (14px effective)', () => {
    expect(useLod(1.0)).toBe('full')
  })

  it('returns full at zoom 0.72 (10.08px effective)', () => {
    expect(useLod(0.72)).toBe('full')
  })

  it('returns chip at zoom 0.5 (7px effective)', () => {
    expect(useLod(0.5)).toBe('chip')
  })

  it('returns chip at zoom 0.71 (9.94px effective)', () => {
    expect(useLod(0.71)).toBe('chip')
  })

  it('returns full at very high zoom', () => {
    expect(useLod(3.0)).toBe('full')
  })
})
