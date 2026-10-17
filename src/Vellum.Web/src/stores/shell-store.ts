import { create } from 'zustand'
import type { ImpactCertainty } from '@/lib/import-api'
import { AREAS, areaOf, type Area, type Flyout } from '@/lib/areas'

export type { Area, Flyout }

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

/** The tabs of the bell panel: the inbox of the person, and the comment threads of the open project. */
export type InboxTab = 'inbox' | 'comments'

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
  /** The tool that each area showed last. The area opens on it again. */
  areaTools: Partial<Record<Area, Flyout>>
  /** True while the bell panel is open. */
  inboxOpen: boolean
  inboxTab: InboxTab
  /** True when the project tools sidebar shows icons only. It lasts across page loads; reset keeps it. */
  sidebarCollapsed: boolean
  detailPanelOpen: boolean
  selectedElementId: string | null
  selectedRelationshipId: string | null
  selectedMessageId: string | null
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
  /** Opens the area on the tool it showed last, or closes it when it is open. */
  toggleArea: (area: Area) => void
  setInboxOpen: (open: boolean) => void
  setInboxTab: (tab: InboxTab) => void
  /** Opens the bell panel on the comment threads of the open project. */
  openComments: () => void
  setSidebarCollapsed: (collapsed: boolean) => void
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
  showOnCanvas: (highlight: CanvasHighlight) => void
  clearCanvasHighlight: () => void
  /** Opens Review on its Decisions tab with the form of a new decision. */
  startDecision: () => void
  clearDecisionRequest: () => void
  /** Brings a thread into view: in the detail panel of its target, or in the Comments tab of the bell panel. */
  focusThread: (threadId: string | null) => void
  reset: () => void
}

/** The state change that opens a tool, and remembers it as the last tool of its area. */
function opened(state: { areaTools: Partial<Record<Area, Flyout>> }, flyout: Flyout) {
  const area = areaOf(flyout)
  return { activeFlyout: flyout, areaTools: area ? { ...state.areaTools, [area]: flyout } : state.areaTools }
}

export const useShellStore = create<ShellState>((set) => ({
  activeFlyout: null,
  areaTools: {},
  inboxOpen: false,
  inboxTab: 'inbox',
  sidebarCollapsed: readSidebarCollapsed(),
  detailPanelOpen: false,
  selectedElementId: null,
  selectedRelationshipId: null,
  selectedMessageId: null,
  activeViewId: null,
  activeDocId: null,
  commandPaletteOpen: false,
  focusElementId: null,
  canvasHighlight: null,
  newDecisionRequested: false,
  focusThreadId: null,

  openFlyout: (flyout) => set((state) => opened(state, flyout)),
  toggleArea: (area) =>
    set((state) => {
      if (state.activeFlyout && areaOf(state.activeFlyout) === area) return { activeFlyout: null }
      const tool = state.areaTools[area] ?? AREAS.find((a) => a.id === area)!.tools[0].flyout
      return opened(state, tool)
    }),
  setInboxOpen: (open) => set({ inboxOpen: open }),
  setInboxTab: (tab) => set({ inboxTab: tab }),
  openComments: () => set({ inboxOpen: true, inboxTab: 'comments' }),
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
    set((state) => (state.activeFlyout === flyout ? { activeFlyout: null } : opened(state, flyout))),

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
  startDecision: () => set((state) => ({ ...opened(state, 'review'), newDecisionRequested: true })),
  clearDecisionRequest: () => set({ newDecisionRequested: false }),
  focusThread: (threadId) => set({ focusThreadId: threadId }),

  reset: () =>
    set({
      activeFlyout: null,
      areaTools: {},
      inboxOpen: false,
      inboxTab: 'inbox',
      detailPanelOpen: false,
      selectedElementId: null,
      selectedRelationshipId: null,
      selectedMessageId: null,
      activeViewId: null,
      activeDocId: null,
      commandPaletteOpen: false,
      focusElementId: null,
      canvasHighlight: null,
      newDecisionRequested: false,
      focusThreadId: null,
    }),
}))
