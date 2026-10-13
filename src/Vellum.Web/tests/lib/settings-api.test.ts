import { describe, it, expect } from 'vitest'
import { countOverrides, effectiveSection, setOverride, type SettingsDocument } from '../../src/lib/settings-api'

const defaults = {
  cost: { provider: 'aws', region: 'eu-west-1', currency: 'USD' },
  usage: { monthlyAiBudgetUsd: null, maxScansPerDay: 20 },
} as unknown as SettingsDocument

describe('settings overrides', () => {
  it('reads the default until a field is overridden', () => {
    const overrides = setOverride({}, 'cost', 'currency', 'GBP')
    expect(effectiveSection({ defaults }, overrides, 'cost')).toEqual({ provider: 'aws', region: 'eu-west-1', currency: 'GBP' })
    expect(effectiveSection({ defaults }, overrides, 'usage').maxScansPerDay).toBe(20)
  })

  it('removes the section when its last override is reset', () => {
    const overridden = setOverride({}, 'cost', 'currency', 'GBP')
    expect(setOverride(overridden, 'cost', 'currency', undefined)).toEqual({})
  })

  it('keeps an explicit null as an override', () => {
    const overrides = setOverride({}, 'usage', 'monthlyAiBudgetUsd', null)
    expect(countOverrides(overrides)).toBe(1)
  })
})
