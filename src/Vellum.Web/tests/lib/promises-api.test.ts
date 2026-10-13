import { describe, it, expect } from 'vitest'
import { describeCounts, describeResult } from '../../src/lib/promises-api'

describe('promise status wording', () => {
  it('leads with failures and never says verified', () => {
    const text = describeCounts({ passed: 2, failed: 1, stale: 0, notRun: 3, unsupported: 1 })
    expect(text).toBe('1 failed · 2 passed · 3 not run · 1 unsupported')
    expect(text.toLowerCase()).not.toContain('verified')
  })

  it('says when a promise has no obligations', () => {
    expect(describeCounts({ passed: 0, failed: 0, stale: 0, notRun: 0, unsupported: 0 })).toBe('No obligations')
  })

  it('shows a result on inputs that moved as stale, never as passed', () => {
    const stale = describeResult({ result: 'passed', current: false }, true)
    expect(stale.label).toBe('Stale (passed on older inputs)')
    expect(stale.color).not.toBe(describeResult({ result: 'passed', current: true }, true).color)
  })

  it('shows an obligation with no result as not run, and one of a kind Vellum cannot check as unsupported', () => {
    expect(describeResult(undefined, true).label).toBe('Not run')
    expect(describeResult(undefined, false).label).toBe('Unsupported')
  })
})
