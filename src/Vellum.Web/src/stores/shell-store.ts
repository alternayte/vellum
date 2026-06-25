import { create } from 'zustand'

interface ShellState {
  navigatorOpen: boolean
  detailPanelOpen: boolean
  selectedElementId: string | null
  selectedRelationshipId: string | null
  selectedMessageId: string | null
  activeViewId: string | null
  activeDocId: string | null
  commandPaletteOpen: boolean
  focusElementId: string | null

  toggleNavigator: () => void
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
  reset: () => void
}

export const useShellStore = create<ShellState>((set) => ({
  navigatorOpen: true,
  detailPanelOpen: false,
  selectedElementId: null,
  selectedRelationshipId: null,
  selectedMessageId: null,
  activeViewId: null,
  activeDocId: null,
  commandPaletteOpen: false,
  focusElementId: null,

  toggleNavigator: () =>
    set((state) => ({ navigatorOpen: !state.navigatorOpen })),

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

  reset: () =>
    set({
      navigatorOpen: true,
      detailPanelOpen: false,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedMessageId: null,
      activeViewId: null,
      activeDocId: null,
      commandPaletteOpen: false,
      focusElementId: null,
    }),
}))
