import { create } from 'zustand'

interface DrillSegment {
  elementId: string
  name: string
  kind: string
}

type Lens = 'none' | 'status' | 'metadata' | 'messages' | 'facts'

interface CanvasState {
  drillPath: DrillSegment[]
  currentRootId: string | null
  activeLens: Lens
  zoomLevel: number
  expandedNodeIds: Set<string>
  /**
   * Draws the lines the view hides, faint, for this person only. It is not saved: the view keeps
   * its hidden lines for everyone else.
   */
  showHiddenLines: boolean

  drillInto: (element: DrillSegment) => void
  drillTo: (index: number) => void
  setLens: (lens: Lens) => void
  toggleLens: (lens: 'status' | 'metadata' | 'messages' | 'facts') => void
  setZoom: (zoom: number) => void
  toggleExpand: (id: string) => void
  /**
   * Puts the inside of an element on the canvas. `path` is the element's ancestors from the
   * top level down, the element last. An element on the current level expands in place; the
   * canvas goes into any other element. The current level's own element needs nothing.
   */
  showInside: (path: DrillSegment[]) => void
  setShowHiddenLines: (show: boolean) => void
  reset: () => void
}

export const useCanvasStore = create<CanvasState>((set) => ({
  drillPath: [],
  currentRootId: null,
  activeLens: 'none',
  zoomLevel: 1,
  expandedNodeIds: new Set(),
  showHiddenLines: false,

  drillInto: (element) =>
    set((state) => ({
      drillPath: [...state.drillPath, element],
      currentRootId: element.elementId,
    })),

  drillTo: (index) =>
    set((state) => {
      if (index < 0) return { drillPath: [], currentRootId: null }
      const newPath = state.drillPath.slice(0, index + 1)
      return {
        drillPath: newPath,
        currentRootId: newPath[newPath.length - 1]?.elementId ?? null,
      }
    }),

  setLens: (lens) => set({ activeLens: lens }),

  toggleLens: (lens) =>
    set((state) => ({
      activeLens: state.activeLens === lens ? 'none' : lens,
    })),

  setZoom: (zoom) => set({ zoomLevel: zoom }),

  toggleExpand: (id) =>
    set((state) => {
      const next = new Set(state.expandedNodeIds)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return { expandedNodeIds: next }
    }),

  showInside: (path) =>
    set((state) => {
      const target = path[path.length - 1]
      if (!target || target.elementId === state.currentRootId) return {}
      const levelOfTarget = path[path.length - 2]?.elementId ?? null
      if (levelOfTarget === state.currentRootId) {
        return { expandedNodeIds: new Set(state.expandedNodeIds).add(target.elementId) }
      }
      return { drillPath: path, currentRootId: target.elementId }
    }),

  setShowHiddenLines: (show) => set({ showHiddenLines: show }),

  reset: () =>
    set({
      showHiddenLines: false,
      drillPath: [],
      currentRootId: null,
      activeLens: 'none',
      zoomLevel: 1,
      expandedNodeIds: new Set(),
    }),
}))
