import { describe, it, expect, beforeEach } from 'vitest'
import { useShellStore } from '../../src/stores/shell-store'

beforeEach(() => {
  const store = useShellStore.getState()
  store.selectElement(null)
  store.selectRelationship(null)
  store.closeCommandPalette()
})

describe('shell store', () => {
  it('selectElement opens detail panel and clears relationship', () => {
    useShellStore.getState().selectRelationship('r1')
    useShellStore.getState().selectElement('e1')
    const state = useShellStore.getState()
    expect(state.selectedElementId).toBe('e1')
    expect(state.selectedRelationshipId).toBeNull()
    expect(state.detailPanelOpen).toBe(true)
  })

  it('selectElement(null) closes detail panel', () => {
    useShellStore.getState().selectElement('e1')
    useShellStore.getState().selectElement(null)
    const state = useShellStore.getState()
    expect(state.selectedElementId).toBeNull()
    expect(state.detailPanelOpen).toBe(false)
  })

  it('selectRelationship opens detail panel and clears element', () => {
    useShellStore.getState().selectElement('e1')
    useShellStore.getState().selectRelationship('r1')
    const state = useShellStore.getState()
    expect(state.selectedRelationshipId).toBe('r1')
    expect(state.selectedElementId).toBeNull()
    expect(state.detailPanelOpen).toBe(true)
  })

  it('toggleCommandPalette toggles open state', () => {
    useShellStore.getState().toggleCommandPalette()
    expect(useShellStore.getState().commandPaletteOpen).toBe(true)
    useShellStore.getState().toggleCommandPalette()
    expect(useShellStore.getState().commandPaletteOpen).toBe(false)
  })
})
