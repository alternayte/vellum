import { describe, it, expect, beforeEach } from 'vitest'
import { useShellStore } from '../../src/stores/shell-store'

beforeEach(() => {
  useShellStore.getState().reset()
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

  it('openDoc sets activeDocId and clears activeViewId', () => {
    useShellStore.getState().setActiveView('v1')
    useShellStore.getState().openDoc('doc-1')
    const state = useShellStore.getState()
    expect(state.activeDocId).toBe('doc-1')
    expect(state.activeViewId).toBeNull()
  })

  it('closeDoc clears activeDocId', () => {
    useShellStore.getState().openDoc('doc-1')
    useShellStore.getState().closeDoc()
    expect(useShellStore.getState().activeDocId).toBeNull()
  })

  it('setActiveView clears activeDocId', () => {
    useShellStore.getState().openDoc('doc-1')
    useShellStore.getState().setActiveView('v1')
    const state = useShellStore.getState()
    expect(state.activeViewId).toBe('v1')
    expect(state.activeDocId).toBeNull()
  })

  it('reset clears activeDocId', () => {
    useShellStore.getState().openDoc('doc-1')
    useShellStore.getState().reset()
    expect(useShellStore.getState().activeDocId).toBeNull()
  })

  describe('flyout state', () => {
    it('toggleFlyout opens a flyout', () => {
      useShellStore.getState().toggleFlyout('files')
      expect(useShellStore.getState().activeFlyout).toBe('files')
    })

    it('toggleFlyout closes same flyout', () => {
      useShellStore.getState().toggleFlyout('files')
      useShellStore.getState().toggleFlyout('files')
      expect(useShellStore.getState().activeFlyout).toBeNull()
    })

    it('toggleFlyout switches to different flyout', () => {
      useShellStore.getState().toggleFlyout('files')
      useShellStore.getState().toggleFlyout('catalog')
      expect(useShellStore.getState().activeFlyout).toBe('catalog')
    })

    it('openFlyout sets activeFlyout', () => {
      useShellStore.getState().openFlyout('create')
      expect(useShellStore.getState().activeFlyout).toBe('create')
    })

    it('closeFlyout clears activeFlyout', () => {
      useShellStore.getState().openFlyout('files')
      useShellStore.getState().closeFlyout()
      expect(useShellStore.getState().activeFlyout).toBeNull()
    })

    it('reset clears activeFlyout', () => {
      useShellStore.getState().openFlyout('files')
      useShellStore.getState().reset()
      expect(useShellStore.getState().activeFlyout).toBeNull()
    })
  })
})
