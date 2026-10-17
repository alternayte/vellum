import { describe, expect, it } from 'vitest'
import { derivedLineId, projectLines, type LineElement, type LineRelationship } from './derived-lines'
import fixtureFile from '../../../../tests/Vellum.Tests/Fixtures/Diagrams/derived-lines.json'

// The backend test DerivedLinesTests reads the same file, so the canvas and the exports agree.
interface Fixture {
  elements: LineElement[]
  relationships: LineRelationship[]
  cases: {
    name: string
    visible: string[]
    direct: string[]
    derived: { fromId: string; toId: string; relationshipIds: string[]; label: string | null }[]
  }[]
}
const fixture: Fixture = fixtureFile

describe('projectLines', () => {
  for (const c of fixture.cases) {
    it(`draws ${c.name} as the fixture says`, () => {
      const { direct, derived } = projectLines(fixture.elements, fixture.relationships, new Set(c.visible))
      expect(direct.map((r) => r.id).sort()).toEqual(c.direct)
      expect(derived).toEqual(c.derived.map((d) => ({ id: derivedLineId(d.fromId, d.toId), hiddenCount: 0, ...d })))
    })
  }

  it('draws nothing for an end that is not in the view, and survives a cycle of parents', () => {
    const elements = [{ id: 'x', parentId: 'y' }, { id: 'y', parentId: 'x' }, { id: 'v', parentId: null }]
    const relationships = [{ id: 'r', fromId: 'x', toId: 'v', label: 'loops' }]
    expect(projectLines(elements, relationships, new Set(['v']))).toEqual({ direct: [], derived: [] })
  })

  it('merges the hidden lines of a share link into the derived line of the visible cards', () => {
    // a holds a1 (shared); b and c are top-level. The hidden lines run between shared cards.
    const elements = [{ id: 'a', parentId: null }, { id: 'a1', parentId: 'a' }, { id: 'b', parentId: null }, { id: 'c', parentId: null }]
    const relationships = [{ id: 'r1', fromId: 'a1', toId: 'b', label: 'shared' }]
    const hidden = [
      { fromId: 'a1', toId: 'b', label: null, count: 2 },
      { fromId: 'a', toId: 'b', label: 'one hidden', count: 1 },
      { fromId: 'c', toId: 'b', label: 'only one', count: 1 },
      { fromId: 'a1', toId: 'a', label: 'inside', count: 1 },
    ]
    const top = projectLines(elements, relationships, new Set(['a', 'b', 'c']), hidden)
    expect(top.direct).toEqual([])
    expect(top.derived).toEqual([
      { id: derivedLineId('a', 'b'), fromId: 'a', toId: 'b', relationshipIds: ['r1'], hiddenCount: 3, label: '4 relationships' },
      { id: derivedLineId('c', 'b'), fromId: 'c', toId: 'b', relationshipIds: [], hiddenCount: 1, label: 'only one' },
    ])
    // With a open, a1 is a card: the line from a1 holds the shared relationship and two hidden ones.
    const open = projectLines(elements, relationships, new Set(['a', 'a1', 'b', 'c']), hidden)
    expect(open.direct.map((r) => r.id)).toEqual(['r1'])
    expect(open.derived.map((d) => [d.fromId, d.toId, d.relationshipIds.length, d.hiddenCount, d.label])).toEqual([
      ['a', 'b', 0, 1, 'one hidden'],
      ['a1', 'a', 0, 1, 'inside'],
      ['a1', 'b', 0, 2, '2 relationships'],
      ['c', 'b', 0, 1, 'only one'],
    ])
  })
})
