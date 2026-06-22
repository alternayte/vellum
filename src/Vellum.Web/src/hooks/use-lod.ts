const BASE_FONT_SIZE = 14
const LEGIBILITY_THRESHOLD = 10

export type LodTier = 'full' | 'chip'

export function useLod(zoom: number): LodTier {
  const effectiveSize = BASE_FONT_SIZE * zoom
  return effectiveSize >= LEGIBILITY_THRESHOLD ? 'full' : 'chip'
}
