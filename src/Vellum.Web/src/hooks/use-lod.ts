/** Below this zoom a card drops the technology chips of the metadata lens. */
const COMPACT_BELOW = 0.45
/** Below this zoom a card becomes a chip with the name only: a card's text is unreadable. */
const CHIP_BELOW = 0.3

export type LodTier = 'full' | 'compact' | 'chip'

/**
 * Progressive detail: cards show everything when legible, compact cards keep the same shape
 * with name, kind line and description, chips keep only the name. Detail never carries
 * information alone — every tier still shows the fact-status marker.
 */
export function useLod(zoom: number): LodTier {
  if (zoom >= COMPACT_BELOW) return 'full'
  if (zoom >= CHIP_BELOW) return 'compact'
  return 'chip'
}
