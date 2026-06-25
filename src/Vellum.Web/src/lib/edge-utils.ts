import { getBezierPath, getStraightPath, getSmoothStepPath } from '@xyflow/react'

type PathFn = typeof getBezierPath

export function getEdgePathFn(lineShape: string | null | undefined): PathFn {
  switch (lineShape) {
    case 'straight': return getStraightPath as unknown as PathFn
    case 'step': return getSmoothStepPath as unknown as PathFn
    default: return getBezierPath
  }
}

const OFFSET_PX = 30

export function computeEdgeOffset(
  edgeIndex: number,
  totalParallel: number,
): { sourceOffset: number; targetOffset: number } {
  if (totalParallel <= 1) return { sourceOffset: 0, targetOffset: 0 }
  const offset = (edgeIndex - (totalParallel - 1) / 2) * OFFSET_PX
  return { sourceOffset: offset, targetOffset: offset }
}

const LABEL_SPREAD = 0.2
const LABEL_MIN = 0.2
const LABEL_MAX = 0.8

export function computeLabelOffset(
  edgeIndex: number,
  totalParallel: number,
): number {
  if (totalParallel <= 1) return 0.5
  const raw = 0.5 + (edgeIndex - (totalParallel - 1) / 2) * LABEL_SPREAD
  return Math.max(LABEL_MIN, Math.min(LABEL_MAX, raw))
}
