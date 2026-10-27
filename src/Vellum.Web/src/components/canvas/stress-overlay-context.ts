import { createContext, useContext } from 'react'
import type { LoadStatus, StressOverlay } from '@/lib/stress-overlay'

/**
 * The Stress overlay of the canvas, given to the cards and lines below it. Null when no run is
 * open. A card or a line reads its own entry, so the projection of the model is not touched.
 */
export interface StressOverlayValue {
  overlay: StressOverlay
  /** The person may run the profile: an Editor on a model that can change. */
  canFail: boolean
  failing: string | null
}

export const StressOverlayContext = createContext<StressOverlayValue | null>(null)

export const LOAD_COLOR: Record<LoadStatus, string> = {
  ok: 'var(--trace-request)',
  busy: 'var(--trace-retry)',
  saturated: 'var(--trace-failure)',
  down: 'var(--trace-control)',
}

export const LOAD_LABEL: Record<LoadStatus, string> = {
  ok: 'ok',
  busy: 'busy',
  saturated: 'saturated',
  down: 'down',
}

/** The estimated rate of a line, or nothing when the run sent no work along it. */
export function useLineRate(id: string): number | null {
  return useContext(StressOverlayContext)?.overlay.lines.get(id) ?? null
}

