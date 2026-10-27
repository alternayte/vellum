import { create } from 'zustand'
import { stressApi, StressRefused, type StressRun } from '@/lib/stress-api'

/** A stored Stress run that the canvas draws. The run is read once; the overlay changes nothing. */
export interface OpenStressOverlay {
  projectId: string
  /** The draft the run was made on, for a run of a failed card. Null is main. */
  branchId: string | null
  profileName: string
  run: StressRun
  /** The run before the first failed card, to go back to. */
  previous: StressRun | null
}

interface StressOverlayState {
  open: OpenStressOverlay | null
  /** The element whose failure is running now. */
  failing: string | null
  error: string | null

  show: (projectId: string, branchId: string | null, profileName: string, run: StressRun) => void
  close: () => void
  /** Runs the profile of the open run again with the element down for the whole run, and opens the new run. */
  fail: (elementId: string) => Promise<void>
  /** Opens the run before the first failed card again. */
  back: () => void
}

export const useStressOverlayStore = create<StressOverlayState>((set, get) => ({
  open: null,
  failing: null,
  error: null,

  show: (projectId, branchId, profileName, run) =>
    set({ open: { projectId, branchId, profileName, run, previous: null }, failing: null, error: null }),
  close: () => set({ open: null, failing: null, error: null }),

  fail: async (elementId) => {
    const open = get().open
    if (!open || get().failing) return
    set({ failing: elementId, error: null })
    try {
      const run = await stressApi.run(open.projectId, open.run.run.profileId, open.branchId, elementId)
      // The person may have closed the overlay or opened another run meanwhile: the new run is stored and listed, not shown.
      set((s) => s.open?.run.run.id === open.run.run.id
        ? { open: { ...s.open, run, previous: s.open.previous ?? s.open.run }, failing: null }
        : { failing: null })
    } catch (e) {
      const error = e instanceof StressRefused
        ? [e.message, ...e.issues.map((i) => i.message)].join(' ')
        : e instanceof Error ? e.message : 'The run did not complete.'
      set((s) => (s.open?.run.run.id === open.run.run.id ? { failing: null, error } : { failing: null }))
    }
  },

  back: () => set((s) => (s.open?.previous ? { open: { ...s.open, run: s.open.previous, previous: null }, error: null } : {})),
}))
