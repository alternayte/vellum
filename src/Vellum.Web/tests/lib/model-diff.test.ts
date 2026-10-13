import { describe, expect, it } from 'vitest'
import { changedFields, entityLabel } from '../../src/lib/model-diff'
import { scopeNote } from '../../src/lib/scope-note'

describe('changedFields', () => {
  it('lists only the fields that differ, and never the ID or the stale mark', () => {
    const left = { id: 'e1', name: 'Orders', technology: null, tags: ['a'], stale: null }
    const right = { id: 'e1', name: 'Orders API', technology: null, tags: ['a'], stale: { reason: 'x' } }
    expect(changedFields(left, right)).toEqual([{ field: 'name', left: 'Orders', right: 'Orders API' }])
  })

  it('gives every field of the one side when the other side does not have the fact', () => {
    expect(changedFields(null, { id: 'e1', name: 'Outbox', tags: [] })).toEqual([{ field: 'name', left: '—', right: 'Outbox' }])
  })
})

describe('entityLabel', () => {
  const names = (id: string) => ({ a: 'Orders API', b: 'Orders database' })[id]

  it('names a relationship by its two ends', () => {
    expect(entityLabel('relationship', { fromId: 'a', toId: 'b', label: 'Reads' }, names, 'r1')).toBe('Orders API → Orders database · Reads')
  })

  it('falls back to the kind and a short ID when the value is gone', () => {
    expect(entityLabel('operation', null, names, '12345678-aaaa')).toBe('operation 12345678')
  })
})

describe('scopeNote', () => {
  const draft = { kind: 'draft', id: 'd', streamId: 's', name: 'outbox' } as const
  const revision = { kind: 'revision', id: 'r', name: 'before' } as const

  it('says nothing on main', () => {
    expect(scopeNote('build', { kind: 'main' })).toBeNull()
  })

  it('names the panels that read main only', () => {
    for (const flyout of ['files', 'create', 'import', 'promises', 'build'] as const) {
      expect(scopeNote(flyout, draft)).toContain("does not follow the draft 'outbox'")
    }
  })

  it('says nothing for a panel that follows a draft with nothing to add', () => {
    expect(scopeNote('contracts', draft)).toBeNull()
  })

  it('says that a panel shows main while a revision is open', () => {
    expect(scopeNote('contracts', revision)).toContain("shows main, not the revision 'before'")
  })
})
