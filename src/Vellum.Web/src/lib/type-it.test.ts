import { describe, expect, it } from 'vitest'
import { parseTypedLine, planTypedLine, type KnownElement, type KnownRelationship, type TypedLine } from './type-it'

describe('parseTypedLine', () => {
  it('reads an element, a kind and a parent', () => {
    expect(parseTypedLine('  Orders API ')).toEqual({ type: 'element', element: { name: 'Orders API' } })
    expect(parseTypedLine('Customer [person]')).toEqual({ type: 'element', element: { name: 'Customer', kind: 'actor' } })
    expect(parseTypedLine('Orders DB [store] in Shop')).toEqual({ type: 'element', element: { name: 'Orders DB', kind: 'store', parent: 'Shop' } })
    // "in" is only a parent after a kind, so a name may hold the word.
    expect(parseTypedLine('Log in service')).toEqual({ type: 'element', element: { name: 'Log in service' } })
  })

  it('reads a relationship with and without a label', () => {
    expect(parseTypedLine('Web app -> Orders API')).toEqual({ type: 'relationship', from: { name: 'Web app' }, to: { name: 'Orders API' }, label: undefined })
    expect(parseTypedLine('Web app -> Orders API: places orders')).toEqual({
      type: 'relationship', from: { name: 'Web app' }, to: { name: 'Orders API' }, label: 'places orders',
    })
    expect(parseTypedLine('Orders API → Orders DB [db] in Shop: reads and writes')).toEqual({
      type: 'relationship', from: { name: 'Orders API' }, to: { name: 'Orders DB', kind: 'store', parent: 'Shop' }, label: 'reads and writes',
    })
  })

  it('skips blank lines and comments, and explains a line it cannot read', () => {
    expect(parseTypedLine('   ')).toBeNull()
    expect(parseTypedLine('# notes')).toBeNull()
    expect(parseTypedLine('A -> B -> C')).toMatchObject({ type: 'error' })
    expect(parseTypedLine('-> B')).toMatchObject({ type: 'error', message: expect.stringContaining('Before the arrow') })
    expect(parseTypedLine('A -> a')).toMatchObject({ type: 'error' })
    expect(parseTypedLine('Thing [gadget]')).toMatchObject({ type: 'error', message: expect.stringContaining('no kind') })
    expect(parseTypedLine('Orders: the shop')).toMatchObject({ type: 'error' })
  })
})

describe('planTypedLine', () => {
  const plan = (text: string, elements: KnownElement[] = [], relationships: KnownRelationship[] = []) => {
    let n = 0
    const line = parseTypedLine(text) as Exclude<TypedLine, { type: 'error' } | null>
    return planTypedLine(line, { elements, relationships }, { defaultSystem: 'My system', newId: () => `id-${++n}` })
  }

  it('makes systems of plain names and joins them', () => {
    expect(plan('Web app -> Orders API: places orders')).toEqual({ writes: [
      { type: 'element', id: 'id-1', name: 'Web app', kind: 'system', parentId: null },
      { type: 'element', id: 'id-2', name: 'Orders API', kind: 'system', parentId: null },
      { type: 'relationship', id: 'id-3', fromId: 'id-1', toId: 'id-2', label: 'places orders' },
    ] })
  })

  it('reuses an element by name and writes nothing for a known relationship', () => {
    const elements = [
      { id: 'web', name: 'Web app', kind: 'system', parentId: null },
      { id: 'api', name: 'Orders API', kind: 'app', parentId: 'shop' },
    ]
    expect(plan('web APP -> orders api [store]', elements)).toEqual({ writes: [
      { type: 'relationship', id: 'id-1', fromId: 'web', toId: 'api', label: null },
    ] })
    expect(plan('Web app -> Orders API', elements, [{ fromId: 'web', toId: 'api', label: null }])).toEqual({ writes: [] })
  })

  it('puts an app or a store into the named system, or the default system', () => {
    expect(plan('Orders DB [store]')).toEqual({ writes: [
      { type: 'element', id: 'id-1', name: 'My system', kind: 'system', parentId: null },
      { type: 'element', id: 'id-2', name: 'Orders DB', kind: 'store', parentId: 'id-1' },
    ] })
    const shop = { id: 'shop', name: 'Shop', kind: 'system', parentId: null }
    expect(plan('Orders API [app] in shop', [shop])).toEqual({ writes: [
      { type: 'element', id: 'id-1', name: 'Orders API', kind: 'app', parentId: 'shop' },
    ] })
  })

  it('refuses what the containment rules do not allow', () => {
    const customer = { id: 'c', name: 'Customer', kind: 'actor', parentId: null }
    expect(plan('Orders DB [store] in Customer', [customer])).toEqual({ error: expect.stringContaining('Customer is an actor') })
    expect(plan('Handler [component]')).toEqual({ error: expect.stringContaining('sits in an app') })
    expect(plan('Payments [system] in Shop')).toEqual({ error: expect.stringContaining('top level') })
  })

  it('puts a component into its app, and the new app into the default system', () => {
    expect(plan('Handler [component] in Orders API')).toEqual({ writes: [
      { type: 'element', id: 'id-1', name: 'My system', kind: 'system', parentId: null },
      { type: 'element', id: 'id-2', name: 'Orders API', kind: 'app', parentId: 'id-1' },
      { type: 'element', id: 'id-3', name: 'Handler', kind: 'component', parentId: 'id-2' },
    ] })
  })
})
