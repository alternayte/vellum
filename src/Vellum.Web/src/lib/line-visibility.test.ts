import { describe, expect, it } from 'vitest'
import { ALL_LINES_SHOWN, applyLineVisibility, isCrossLevel, type LineVisibility } from './line-visibility'

// shop holds api and db; bank holds ledger. web is top level.
const elements = [
  { id: 'shop', parentId: null },
  { id: 'api', parentId: 'shop' },
  { id: 'db', parentId: 'shop' },
  { id: 'bank', parentId: null },
  { id: 'ledger', parentId: 'bank' },
  { id: 'web', parentId: null },
]
const relationships = [
  { id: 'r-api-db', fromId: 'api', toId: 'db', label: 'reads', kind: 'structural' },
  { id: 'r-web-api', fromId: 'web', toId: 'api', label: 'calls', kind: 'behavioural' },
  { id: 'r-api-ledger', fromId: 'api', toId: 'ledger', label: 'charges', kind: 'behavioural' },
  { id: 'r-db-ledger', fromId: 'db', toId: 'ledger', label: 'syncs', kind: 'structural' },
  { id: 'r-web-bank', fromId: 'web', toId: 'bank', label: 'pays', kind: 'structural' },
]
const parentOf = new Map(elements.map((e) => [e.id, e.parentId]))
// The top level with shop open: its children are on the canvas, bank stays closed.
const visible = new Set(['shop', 'api', 'db', 'bank', 'web'])
const ids = (list: { id: string }[]) => list.map((l) => l.id).sort()
const shown = (patch: Partial<LineVisibility>) => applyLineVisibility(elements, relationships, visible, { ...ALL_LINES_SHOWN, ...patch })

describe('isCrossLevel', () => {
  it('is true only when the two ends sit at different nesting depths', () => {
    expect(isCrossLevel(relationships[1], parentOf)).toBe(true) // web (0) to api (1)
    expect(isCrossLevel(relationships[0], parentOf)).toBe(false) // api (1) to db (1)
    expect(isCrossLevel(relationships[4], parentOf)).toBe(false) // web (0) to bank (0)
  })

  it('survives a cycle of parents', () => {
    const loop = new Map<string, string | null>([['x', 'y'], ['y', 'x'], ['z', null]])
    expect(isCrossLevel({ id: 'r', fromId: 'x', toId: 'z' }, loop)).toBe(true)
  })
})

describe('applyLineVisibility', () => {
  it('draws every line and hides none when the view hides nothing', () => {
    const lines = shown({})
    expect(ids(lines.direct)).toEqual(['r-api-db', 'r-web-api', 'r-web-bank'])
    expect(lines.derived.map((l) => [l.fromId, l.toId, l.relationshipIds])).toEqual([
      ['api', 'bank', ['r-api-ledger']],
      ['db', 'bank', ['r-db-ledger']],
    ])
    expect(lines.hiddenDirect).toEqual([])
    expect(lines.hiddenDerived).toEqual([])
  })

  it('leaves out cross-level lines and keeps lines between ends at one depth', () => {
    const lines = shown({ crossLevel: false })
    expect(ids(lines.direct)).toEqual(['r-api-db', 'r-web-bank'])
    expect(ids(lines.hiddenDirect)).toEqual(['r-web-api'])
    expect(lines.derived).toHaveLength(2)
  })

  it('leaves a relationship hidden by kind or by ID out of the derived lines too', () => {
    const lines = shown({ behavioural: false, hiddenRelationshipIds: ['r-api-db'] })
    expect(ids(lines.direct)).toEqual(['r-web-bank'])
    expect(lines.derived.map((l) => l.id)).toEqual(['derived:db>bank'])
    expect(ids(lines.hiddenDirect)).toEqual(['r-api-db', 'r-web-api'])
    expect(lines.hiddenDerived.map((l) => l.id)).toEqual(['derived:api>bank'])
  })

  it('hides a derived line by its pair of cards, and all of them with the switch', () => {
    const one = shown({ hiddenDerivedLines: [{ fromId: 'api', toId: 'bank' }] })
    expect(one.derived.map((l) => l.id)).toEqual(['derived:db>bank'])
    expect(one.hiddenDerived.map((l) => l.id)).toEqual(['derived:api>bank'])
    // The pair keys the line: the other direction is another line and shows.
    expect(shown({ hiddenDerivedLines: [{ fromId: 'bank', toId: 'api' }] }).derived).toHaveLength(2)

    const none = shown({ derivedLines: false })
    expect(none.derived).toEqual([])
    expect(none.hiddenDerived).toHaveLength(2)
    expect(ids(none.direct)).toEqual(['r-api-db', 'r-web-api', 'r-web-bank'])
  })
})
