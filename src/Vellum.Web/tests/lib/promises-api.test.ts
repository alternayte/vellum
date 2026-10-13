import { describe, it, expect } from 'vitest'
import { describeCounts } from '../../src/lib/promises-api'

describe('promise status wording', () => {
  it('leads with failures and never says verified', () => {
    const text = describeCounts({ passed: 2, failed: 1, notRun: 3, unsupported: 1 })
    expect(text).toBe('1 failed · 2 passed · 3 not run · 1 unsupported')
    expect(text.toLowerCase()).not.toContain('verified')
  })

  it('says when a promise has no obligations', () => {
    expect(describeCounts({ passed: 0, failed: 0, notRun: 0, unsupported: 0 })).toBe('No obligations')
  })
})
