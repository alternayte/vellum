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
      expect(derived).toEqual(c.derived.map((d) => ({ id: derivedLineId(d.fromId, d.toId), ...d })))
    })
  }

  it('draws nothing for an end that is not in the view, and survives a cycle of parents', () => {
    const elements = [{ id: 'x', parentId: 'y' }, { id: 'y', parentId: 'x' }, { id: 'v', parentId: null }]
    const relationships = [{ id: 'r', fromId: 'x', toId: 'v', label: 'loops' }]
    expect(projectLines(elements, relationships, new Set(['v']))).toEqual({ direct: [], derived: [] })
  })
})
