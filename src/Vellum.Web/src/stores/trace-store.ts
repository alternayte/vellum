import { create } from 'zustand'
import type { TraceEvent, TraceRun } from '@/lib/trace'

export const TRACE_SPEEDS = [0.5, 1, 2, 4] as const

interface TraceState {
  run: TraceRun | null
  /** Index of the current event; -1 is the moment before the first one. */
  cursor: number
  playing: boolean
  speed: number

  load: (run: TraceRun) => void
  close: () => void
  play: () => void
  pause: () => void
  seek: (index: number) => void
  step: (delta: 1 | -1) => void
  setSpeed: (speed: number) => void
}

export const useTraceStore = create<TraceState>((set, get) => ({
  run: null,
  cursor: -1,
  playing: false,
  speed: 1,

  load: (run) => set({ run, cursor: -1, playing: false }),
  close: () => set({ run: null, cursor: -1, playing: false }),

  play: () => {
    const { run, cursor } = get()
    if (!run || run.events.length === 0) return
    // Play from the end means play again.
    set({ playing: true, cursor: cursor >= run.events.length - 1 ? -1 : cursor })
  },
  pause: () => set({ playing: false }),

  seek: (index) => {
    const { run } = get()
    if (!run) return
    set({ cursor: Math.max(-1, Math.min(run.events.length - 1, index)), playing: false })
  },

  step: (delta) => {
    const { run, cursor, playing } = get()
    if (!run) return
    const next = Math.max(-1, Math.min(run.events.length - 1, cursor + delta))
    // Automatic playback stops itself on the last event.
    set({ cursor: next, playing: playing && next < run.events.length - 1 })
  },

  setSpeed: (speed) => set({ speed }),
}))

export function currentTraceEvent(state: Pick<TraceState, 'run' | 'cursor'>): TraceEvent | null {
  return state.run && state.cursor >= 0 ? (state.run.events[state.cursor] ?? null) : null
}
