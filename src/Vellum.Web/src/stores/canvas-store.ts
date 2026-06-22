import { create } from 'zustand'

interface DrillSegment {
  elementId: string
  name: string
  kind: string
}

type Lens = 'none' | 'status' | 'metadata'

interface CanvasState {
  drillPath: DrillSegment[]
  currentRootId: string | null
  activeLens: Lens
  zoomLevel: number

  drillInto: (element: DrillSegment) => void
  drillTo: (index: number) => void
  setLens: (lens: Lens) => void
  toggleLens: (lens: 'status' | 'metadata') => void
  setZoom: (zoom: number) => void
  reset: () => void
}

export const useCanvasStore = create<CanvasState>((set) => ({
  drillPath: [],
  currentRootId: null,
  activeLens: 'none',
  zoomLevel: 1,

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

  reset: () =>
    set({
      drillPath: [],
      currentRootId: null,
      activeLens: 'none',
      zoomLevel: 1,
    }),
}))
