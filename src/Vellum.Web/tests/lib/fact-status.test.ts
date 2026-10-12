import { describe, it, expect } from 'vitest'
import {
  factStatusColor,
  factStatusLabel,
  isUnverified,
  provenanceSummary,
} from '../../src/lib/fact-status'

describe('fact status', () => {
  it('labels every status', () => {
    expect(factStatusLabel('declared')).toBe('Declared')
    expect(factStatusLabel('observed-in-source')).toBe('Observed in source')
    expect(factStatusLabel('inferred')).toBe('Inferred')
    expect(factStatusLabel('measured')).toBe('Measured')
    expect(factStatusLabel('unknown')).toBe('Unknown')
  })

  it('falls back to the unknown colour for unrecognised statuses', () => {
    expect(factStatusColor('nonsense')).toBe(factStatusColor('unknown'))
  })

  it('treats inferred and unknown as unverified', () => {
    expect(isUnverified('inferred')).toBe(true)
    expect(isUnverified('unknown')).toBe(true)
    expect(isUnverified('observed-in-source')).toBe(false)
    expect(isUnverified(null)).toBe(false)
  })

  it('summarises provenance with source, revision and confidence', () => {
    const summary = provenanceSummary({
      source: 'github.com/acme/orders@abc123:src/Orders.cs:1-10',
      revision: 'abc123',
      confidence: 0.75,
      acceptedBy: null,
      acceptedAt: null,
    })
    expect(summary).toContain('github.com/acme/orders@abc123:src/Orders.cs:1-10')
    expect(summary).toContain('abc123')
    expect(summary).toContain('75% confidence')
  })

  it('returns null for absent provenance', () => {
    expect(provenanceSummary(null)).toBeNull()
    expect(
      provenanceSummary({
        source: null,
        revision: null,
        confidence: null,
        acceptedBy: null,
        acceptedAt: null,
      }),
    ).toBeNull()
  })
})
