import { create } from 'zustand'

interface ShellState {
  navigatorOpen: boolean
  detailPanelOpen: boolean
  selectedElementId: string | null
  selectedRelationshipId: string | null
  activeViewId: string | null
  commandPaletteOpen: boolean

  toggleNavigator: () => void
  selectElement: (id: string | null) => void
  selectRelationship: (id: string | null) => void
  setActiveView: (id: string | null) => void
  toggleCommandPalette: () => void
  closeCommandPalette: () => void
  reset: () => void
}

export const useShellStore = create<ShellState>((set) => ({
  navigatorOpen: true,
  detailPanelOpen: false,
  selectedElementId: null,
  selectedRelationshipId: null,
  activeViewId: null,
  commandPaletteOpen: false,

  toggleNavigator: () =>
    set((state) => ({ navigatorOpen: !state.navigatorOpen })),

  selectElement: (id) =>
    set({
      selectedElementId: id,
      selectedRelationshipId: null,
      detailPanelOpen: id !== null,
    }),

  selectRelationship: (id) =>
    set({
      selectedRelationshipId: id,
      selectedElementId: null,
      detailPanelOpen: id !== null,
    }),

  setActiveView: (id) => set({ activeViewId: id }),

  toggleCommandPalette: () =>
    set((state) => ({ commandPaletteOpen: !state.commandPaletteOpen })),

  closeCommandPalette: () => set({ commandPaletteOpen: false }),

  reset: () =>
    set({
      navigatorOpen: true,
      detailPanelOpen: false,
      selectedElementId: null,
      selectedRelationshipId: null,
      activeViewId: null,
      commandPaletteOpen: false,
    }),
}))
