import { describe, it, expect } from 'vitest'
import { defaultIconForKind, ARCHITECTURE_ICONS } from '../../src/lib/default-icons'

describe('defaultIconForKind', () => {
  it('returns "database" for store', () => {
    expect(defaultIconForKind('store')).toBe('database')
  })

  it('returns "server" for app', () => {
    expect(defaultIconForKind('app')).toBe('server')
  })

  it('returns "monitor" for system', () => {
    expect(defaultIconForKind('system')).toBe('monitor')
  })

  it('returns "user" for actor', () => {
    expect(defaultIconForKind('actor')).toBe('user')
  })

  it('returns "component" for component', () => {
    expect(defaultIconForKind('component')).toBe('component')
  })

  it('returns "box" for unknown kind', () => {
    expect(defaultIconForKind('unknown')).toBe('box')
  })
})

describe('ARCHITECTURE_ICONS', () => {
  it('contains at least 20 icons', () => {
    expect(ARCHITECTURE_ICONS.length).toBeGreaterThanOrEqual(20)
  })

  it('each entry has name and icon', () => {
    for (const entry of ARCHITECTURE_ICONS) {
      expect(entry.name).toBeTruthy()
      expect(entry.icon).toBeTruthy()
    }
  })
})
