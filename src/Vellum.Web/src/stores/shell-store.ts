import { create } from 'zustand'
import type { ImpactCertainty } from '@/lib/import-api'

/** One step of an affected flow, drawn on the canvas from where it starts to what it reaches. */
export interface HighlightStep {
  stepId: string
  /** The step's place in the flow, from 1. */
  order: number
  label: string | null
  certainty: ImpactCertainty
  fromId: string | null
  toIds: string[]
  /** The message of a publish step: the canvas draws it to the message's consumers when the step names no handler. */
  messageId: string | null
}

export interface CanvasHighlight {
  title: string
  elementIds: string[]
  /** How sure the impact is of each element, for an affected flow. Without it every ring is the same. */
  marks?: Record<string, ImpactCertainty>
  /** The affected steps of a flow, in order. */
  steps?: HighlightStep[]
}

export type Flyout = 'files' | 'create' | 'catalog' | 'contracts' | 'scenarios' | 'import' | 'assistant' | 'promises' | 'review' | 'build' | 'stress' | 'costs' | 'comments' | 'search'

const SIDEBAR_COLLAPSED_KEY = 'vellum-sidebar-collapsed'

function readSidebarCollapsed(): boolean {
  try {
    return localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === '1'
  } catch {
    return false
  }
}

interface ShellState {
  activeFlyout: Flyout | null
  /** True when the project tools sidebar shows icons only. It lasts across page loads; reset keeps it. */
  sidebarCollapsed: boolean
  detailPanelOpen: boolean
  selectedElementId: string | null
  selectedRelationshipId: string | null
  selectedMessageId: string | null
  /** The item of the open ghost layer whose ghost is selected. Its rationale shows in the detail panel. */
  selectedGhostItemId: string | null
  activeViewId: string | null
  activeDocId: string | null
  commandPaletteOpen: boolean
  focusElementId: string | null
  /** Elements to show on the canvas for a promise or an affected flow. The canvas rings them and dims the rest. */
  canvasHighlight: CanvasHighlight | null
  /** True from "New decision (ADR)" until the Review flyout opens the form of a new decision. */
  newDecisionRequested: boolean
  /** The comment thread to bring into view and mark, from a canvas marker, the inbox or a link. */
  focusThreadId: string | null

  openFlyout: (flyout: Flyout) => void
  setSidebarCollapsed: (collapsed: boolean) => void
  closeFlyout: () => void
  toggleFlyout: (flyout: Flyout) => void
  selectElement: (id: string | null) => void
  selectRelationship: (id: string | null) => void
  selectMessage: (id: string | null) => void
  selectGhost: (itemId: string | null) => void
  setActiveView: (id: string | null) => void
  openDoc: (id: string) => void
  closeDoc: () => void
  toggleCommandPalette: () => void
  closeCommandPalette: () => void
  focusElement: (id: string) => void
  clearFocusElement: () => void
  showOnCanvas: (highlight: CanvasHighlight) => void
  clearCanvasHighlight: () => void
  /** Opens Review on its Decisions tab with the form of a new decision. */
  startDecision: () => void
  clearDecisionRequest: () => void
  /** Brings a thread into view: in the detail panel of its target, or in the Comments flyout. */
  focusThread: (threadId: string | null) => void
  reset: () => void
}

export const useShellStore = create<ShellState>((set) => ({
  activeFlyout: null,
  sidebarCollapsed: readSidebarCollapsed(),
  detailPanelOpen: false,
  selectedElementId: null,
  selectedRelationshipId: null,
  selectedMessageId: null,
  selectedGhostItemId: null,
  activeViewId: null,
  activeDocId: null,
  commandPaletteOpen: false,
  focusElementId: null,
  canvasHighlight: null,
  newDecisionRequested: false,
  focusThreadId: null,

  openFlyout: (flyout) => set({ activeFlyout: flyout }),
  setSidebarCollapsed: (collapsed) => {
    set({ sidebarCollapsed: collapsed })
    try {
      localStorage.setItem(SIDEBAR_COLLAPSED_KEY, collapsed ? '1' : '0')
    } catch {
      // Storage is not available. The state lasts for this page load.
    }
  },
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
      selectedGhostItemId: null,
      detailPanelOpen: id !== null,
    }),

  selectRelationship: (id) =>
    set({
      selectedRelationshipId: id,
      selectedElementId: null,
      selectedMessageId: null,
      selectedGhostItemId: null,
      detailPanelOpen: id !== null,
    }),

  selectMessage: (id) =>
    set({
      selectedMessageId: id,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedGhostItemId: null,
      detailPanelOpen: id !== null,
    }),

  selectGhost: (itemId) =>
    set({
      selectedGhostItemId: itemId,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedMessageId: null,
      detailPanelOpen: itemId !== null,
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
  startDecision: () => set({ activeFlyout: 'review', newDecisionRequested: true }),
  clearDecisionRequest: () => set({ newDecisionRequested: false }),
  focusThread: (threadId) => set({ focusThreadId: threadId }),

  reset: () =>
    set({
      activeFlyout: null,
      detailPanelOpen: false,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedMessageId: null,
      selectedGhostItemId: null,
      activeViewId: null,
      activeDocId: null,
      commandPaletteOpen: false,
      focusElementId: null,
      canvasHighlight: null,
      newDecisionRequested: false,
      focusThreadId: null,
    }),
}))
