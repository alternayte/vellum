import { describe, it, expect } from 'vitest'
import { useLod } from '../../src/hooks/use-lod'

describe('useLod', () => {
  it('returns full at zoom 1.0', () => {
    expect(useLod(1.0)).toBe('full')
  })

  it('returns full at very high zoom', () => {
    expect(useLod(3.0)).toBe('full')
  })

  // A fit of a model of a few levels lands between 0.5 and 0.8: the cards stay cards there.
  it('returns full at a typical fit zoom', () => {
    expect(useLod(0.6)).toBe('full')
    expect(useLod(0.45)).toBe('full')
  })

  it('returns compact below 0.45', () => {
    expect(useLod(0.44)).toBe('compact')
    expect(useLod(0.3)).toBe('compact')
  })

  it('returns chip below 0.3', () => {
    expect(useLod(0.29)).toBe('chip')
    expect(useLod(0.1)).toBe('chip')
  })
})
