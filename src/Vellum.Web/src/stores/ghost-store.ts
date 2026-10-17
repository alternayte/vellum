import { create } from 'zustand'
import type { ArchitectureProposal } from '@/lib/design-api'
import type { GhostPlace } from '@/lib/ghost-layer'
import { useShellStore } from '@/stores/shell-store'

/** A decision the server refused: what it asked to accept, and the reasons for each refused item. */
export interface GhostRefusal {
  accept: string[]
  reasons: Map<string, string[]>
}

/** A saved place of an element in the open view. */
export interface ViewPlace {
  elementId: string
  x: number
  y: number
  width?: number
  height?: number
}

export type ViewPlaceWriter = (places: ViewPlace[], sizes: Map<string, { width: number; height: number }>) => void

interface GhostState {
  projectId: string | null
  /** The proposal whose pending items the canvas shows as ghosts. Null when no layer is open. */
  proposal: ArchitectureProposal | null
  refusal: GhostRefusal | null
  /** The last error of a decision that was not a refusal. */
  error: string | null
  busy: boolean
  /** Where the canvas drew each ghost on the current level, from the corner of the node it sits in. */
  places: Map<string, GhostPlace>
  /** The size each open card of the level takes around its ghosts. */
  hostSizes: Map<string, { width: number; height: number }>
  /** Cards of the level without a saved place, where the canvas draws them now. */
  unsaved: Map<string, { x: number; y: number }>
  /**
   * Saves places of the open view: new places, and new sizes of elements that have a place.
   * The workbench sets it; an accepted item keeps the place of its ghost.
   */
  saveViewPlaces: ViewPlaceWriter | null

  open: (projectId: string, proposal: ArchitectureProposal) => void
  update: (proposal: ArchitectureProposal) => void
  setRefusal: (refusal: GhostRefusal | null) => void
  setError: (error: string | null) => void
  setBusy: (busy: boolean) => void
  setPlaces: (places: Map<string, GhostPlace>, hostSizes: Map<string, { width: number; height: number }>, unsaved: Map<string, { x: number; y: number }>) => void
  setViewPlaceWriter: (save: ViewPlaceWriter | null) => void
  close: () => void
}

const empty = { proposal: null, refusal: null, error: null, busy: false, places: new Map(), hostSizes: new Map(), unsaved: new Map() }

export const useGhostStore = create<GhostState>((set) => ({
  projectId: null,
  ...empty,
  saveViewPlaces: null,

  // The layer is drawn on the canvas, so a document gives way to the view and a highlight ends.
  open: (projectId, proposal) => {
    const shell = useShellStore.getState()
    if (shell.activeDocId) shell.closeDoc()
    shell.clearCanvasHighlight()
    shell.selectGhost(null)
    set({ ...empty, projectId, proposal })
  },
  update: (proposal) => set((state) => (state.proposal?.id === proposal.id ? { proposal } : {})),
  setRefusal: (refusal) => set({ refusal }),
  setError: (error) => set({ error }),
  setBusy: (busy) => set({ busy }),
  setPlaces: (places, hostSizes, unsaved) => set({ places, hostSizes, unsaved }),
  setViewPlaceWriter: (saveViewPlaces) => set({ saveViewPlaces }),
  close: () => {
    if (useShellStore.getState().selectedGhostItemId) useShellStore.getState().selectGhost(null)
    set({ ...empty, projectId: null })
  },
}))
