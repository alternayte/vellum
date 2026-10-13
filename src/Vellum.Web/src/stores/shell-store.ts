import { create } from 'zustand'

export type Flyout = 'files' | 'create' | 'catalog' | 'contracts' | 'scenarios' | 'import' | 'assistant' | 'promises' | 'review' | 'build' | 'stress' | 'costs' | 'search'

interface ShellState {
  activeFlyout: Flyout | null
  detailPanelOpen: boolean
  selectedElementId: string | null
  selectedRelationshipId: string | null
  selectedMessageId: string | null
  activeViewId: string | null
  activeDocId: string | null
  commandPaletteOpen: boolean
  focusElementId: string | null
  /** Elements to show on the canvas for a promise or an affected flow. The canvas rings them and dims the rest. */
  canvasHighlight: { title: string; elementIds: string[] } | null

  openFlyout: (flyout: Flyout) => void
  closeFlyout: () => void
  toggleFlyout: (flyout: Flyout) => void
  selectElement: (id: string | null) => void
  selectRelationship: (id: string | null) => void
  selectMessage: (id: string | null) => void
  setActiveView: (id: string | null) => void
  openDoc: (id: string) => void
  closeDoc: () => void
  toggleCommandPalette: () => void
  closeCommandPalette: () => void
  focusElement: (id: string) => void
  clearFocusElement: () => void
  showOnCanvas: (highlight: { title: string; elementIds: string[] }) => void
  clearCanvasHighlight: () => void
  reset: () => void
}

export const useShellStore = create<ShellState>((set) => ({
  activeFlyout: null,
  detailPanelOpen: false,
  selectedElementId: null,
  selectedRelationshipId: null,
  selectedMessageId: null,
  activeViewId: null,
  activeDocId: null,
  commandPaletteOpen: false,
  focusElementId: null,
  canvasHighlight: null,

  openFlyout: (flyout) => set({ activeFlyout: flyout }),
  closeFlyout: () => set({ activeFlyout: null }),
  toggleFlyout: (flyout) =>
    set((state) => ({
      activeFlyout: state.activeFlyout === flyout ? null : flyout,
    })),

  selectElement: (id) =>
    set({
      selectedElementId: id,
      selectedRelationshipId: null,
      selectedMessageId: null,
      detailPanelOpen: id !== null,
    }),

  selectRelationship: (id) =>
    set({
      selectedRelationshipId: id,
      selectedElementId: null,
      selectedMessageId: null,
      detailPanelOpen: id !== null,
    }),

  selectMessage: (id) =>
    set({
      selectedMessageId: id,
      selectedElementId: null,
      selectedRelationshipId: null,
      detailPanelOpen: id !== null,
    }),

  setActiveView: (id) => set({ activeViewId: id, activeDocId: null }),

  openDoc: (id) => set({ activeDocId: id, activeViewId: null }),
  closeDoc: () => set({ activeDocId: null }),

  toggleCommandPalette: () =>
    set((state) => ({ commandPaletteOpen: !state.commandPaletteOpen })),

  closeCommandPalette: () => set({ commandPaletteOpen: false }),

  focusElement: (id) => set({ focusElementId: id }),
  clearFocusElement: () => set({ focusElementId: null }),
  // The canvas shows the highlight: not a doc, and with no flyout over it.
  showOnCanvas: (highlight) => set({ canvasHighlight: highlight, activeDocId: null, activeFlyout: null }),
  clearCanvasHighlight: () => set({ canvasHighlight: null }),

  reset: () =>
    set({
      activeFlyout: null,
      detailPanelOpen: false,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedMessageId: null,
      activeViewId: null,
      activeDocId: null,
      commandPaletteOpen: false,
      focusElementId: null,
      canvasHighlight: null,
    }),
}))
