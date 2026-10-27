import { describe, expect, it } from 'vitest'
import { dropPlace } from '@/lib/element-palette'

const system = { id: 's', kind: 'system', external: false }
const app = { id: 'a', kind: 'app', external: false }
const external = { id: 'x', kind: 'system', external: true }

describe('dropPlace', () => {
  it('puts a store inside the system under the pointer', () => {
    expect(dropPlace('store', [system], null)).toEqual({ parentId: 's', cardId: 's' })
  })

  it('passes over an inner card that cannot hold the kind to the card around it', () => {
    expect(dropPlace('store', [app, system], null)).toEqual({ parentId: 's', cardId: 's' })
    expect(dropPlace('component', [app, system], null)).toEqual({ parentId: 'a', cardId: 'a' })
  })

  it('puts an element on the level the canvas shows when no card under the pointer holds it', () => {
    expect(dropPlace('system', [system], null)).toEqual({ parentId: null, cardId: null })
    expect(dropPlace('store', [], { id: 's', kind: 'system' })).toEqual({ parentId: 's', cardId: null })
  })

  it('offers no inside in an external system', () => {
    expect(dropPlace('store', [external], null)).toEqual({ refused: 'A store goes inside a system. Drop it on a system card.' })
  })

  it('refuses a kind that neither the card nor the level holds, with the rule', () => {
    expect(dropPlace('component', [], null)).toEqual({ refused: 'A component goes inside an app. Drop it on an app card.' })
    expect(dropPlace('actor', [], { id: 's', kind: 'system' })).toEqual({ refused: 'An actor goes on the top level. Go back to the top level to add one.' })
  })
})
