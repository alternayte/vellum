const BASE_FONT_SIZE = 14
const LEGIBILITY_THRESHOLD = 10
const COMPACT_THRESHOLD = 12.5

export type LodTier = 'full' | 'compact' | 'chip'

/**
 * Progressive detail: cards show everything when legible, compact cards keep identity and
 * truth labels, chips keep only the name. Detail never carries information alone — every
 * tier still shows the fact-status marker.
 */
export function useLod(zoom: number): LodTier {
  const effectiveSize = BASE_FONT_SIZE * zoom
  if (effectiveSize >= COMPACT_THRESHOLD) return 'full'
  if (effectiveSize >= LEGIBILITY_THRESHOLD) return 'compact'
  return 'chip'
}
