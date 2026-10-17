import { describe, expect, it } from 'vitest'
import { withDependencies } from './ghost-layer'
import type { ArchitectureItem } from './design-api'

const item = (id: string, dependsOn: string[], status: ArchitectureItem['status'] = 'pending'): ArchitectureItem => ({
  id, alias: id, action: 'add', entityKind: 'element', name: id, proposed: {}, dependsOn, confidence: 0.8, rationale: '', status,
})

// A system S holds an app A, which holds a component C. R joins C to an existing part. X stands alone.
const items = [item('S', []), item('A', ['S']), item('C', ['A']), item('R', ['C']), item('X', [])]

describe('withDependencies', () => {
  it('takes what an item depends on, through every step', () => {
    expect([...withDependencies(['R'], items)].sort()).toEqual(['A', 'C', 'R', 'S'])
  })

  it('leaves out an accepted dependency and what it depends on', () => {
    const decided = items.map((i) => (i.id === 'A' ? { ...i, status: 'accepted' as const } : i))
    expect([...withDependencies(['R'], decided)].sort()).toEqual(['C', 'R'])
  })

  it('takes nothing for an item that is decided already', () => {
    const decided = items.map((i) => (i.id === 'X' ? { ...i, status: 'rejected' as const } : i))
    expect([...withDependencies(['X'], decided)]).toEqual([])
  })
})
