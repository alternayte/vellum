import { describe, it, expect } from 'vitest'
import { buildDuplicateSet } from '../../src/lib/duplicate'

const el = (id: string, kind = 'system', parentId: string | null = null) => ({
  id, kind, name: `El ${id}`, description: null, technology: null,
  ownerId: null, parentId, status: 'current', tags: [] as string[],
})

const rel = (id: string, fromId: string, toId: string) => ({
  id, fromId, toId, label: null, technology: null,
})

const pos = (elementId: string, x: number, y: number) => ({ elementId, x, y })

describe('buildDuplicateSet', () => {
  it('duplicates a single node with 40px offset', () => {
    const result = buildDuplicateSet(
      ['a'],
      [el('a'), el('b')],
      [rel('r1', 'a', 'b')],
      [pos('a', 100, 200)],
    )
    expect(result.elements).toHaveLength(1)
    expect(result.elements[0].name).toBe('El a (copy)')
    expect(result.elements[0].newId).not.toBe('a')
    expect(result.relationships).toHaveLength(0)
    expect(result.positions).toHaveLength(1)
    expect(result.positions[0].x).toBe(140)
    expect(result.positions[0].y).toBe(240)
  })

  it('duplicates multiple nodes with internal relationships remapped', () => {
    const result = buildDuplicateSet(
      ['a', 'b'],
      [el('a'), el('b'), el('c')],
      [rel('r1', 'a', 'b'), rel('r2', 'a', 'c')],
      [pos('a', 0, 0), pos('b', 300, 0)],
    )
    expect(result.elements).toHaveLength(2)
    expect(result.relationships).toHaveLength(1)

    const newA = result.elements.find((e) => e.name === 'El a (copy)')!
    const newB = result.elements.find((e) => e.name === 'El b (copy)')!
    const newRel = result.relationships[0]

    expect(newRel.fromId).toBe(newA.newId)
    expect(newRel.toId).toBe(newB.newId)
  })

  it('does not duplicate relationships with one endpoint outside selection', () => {
    const result = buildDuplicateSet(
      ['a'],
      [el('a'), el('b')],
      [rel('r1', 'a', 'b')],
      [pos('a', 0, 0)],
    )
    expect(result.relationships).toHaveLength(0)
  })

  it('duplicates container with children recursively', () => {
    const result = buildDuplicateSet(
      ['parent'],
      [el('parent', 'system'), el('child1', 'app', 'parent'), el('child2', 'app', 'parent')],
      [rel('r1', 'child1', 'child2')],
      [pos('parent', 0, 0), pos('child1', 40, 60), pos('child2', 340, 60)],
    )
    expect(result.elements).toHaveLength(3)
    expect(result.relationships).toHaveLength(1)

    const newParent = result.elements.find((e) => e.name === 'El parent (copy)')!
    const newChild1 = result.elements.find((e) => e.name === 'El child1 (copy)')!
    const newChild2 = result.elements.find((e) => e.name === 'El child2 (copy)')!

    expect(newChild1.parentId).toBe(newParent.newId)
    expect(newChild2.parentId).toBe(newParent.newId)

    const newRel = result.relationships[0]
    expect(newRel.fromId).toBe(newChild1.newId)
    expect(newRel.toId).toBe(newChild2.newId)
  })

  it('uses default position when element has no position', () => {
    const result = buildDuplicateSet(
      ['a'],
      [el('a')],
      [],
      [],
    )
    expect(result.positions).toHaveLength(1)
    expect(result.positions[0].x).toBe(40)
    expect(result.positions[0].y).toBe(40)
  })

  it('preserves element properties on duplicates', () => {
    const elements = [{
      id: 'a', kind: 'app', name: 'API', description: 'REST API',
      technology: 'Node.js', ownerId: null, parentId: null, status: 'planned',
      tags: ['backend', 'v2'],
    }]
    const result = buildDuplicateSet(['a'], elements, [], [pos('a', 0, 0)])
    const dup = result.elements[0]
    expect(dup.kind).toBe('app')
    expect(dup.description).toBe('REST API')
    expect(dup.technology).toBe('Node.js')
    expect(dup.status).toBe('planned')
    expect(dup.tags).toEqual(['backend', 'v2'])
  })
})
