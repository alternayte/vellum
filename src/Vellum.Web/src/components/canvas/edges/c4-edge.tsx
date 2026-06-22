import { memo } from 'react'
import {
  getSmoothStepPath,
  EdgeLabelRenderer,
  type Edge,
  type EdgeProps,
} from '@xyflow/react'
import { useCanvasStore } from '@/stores/canvas-store'
import type { DiffState } from '@/stores/draft-store'

export interface C4EdgeData extends Record<string, unknown> {
  label: string | null
  technology: string | null
  diffState?: DiffState
}

const diffEdgeColor: Record<string, string> = {
  added: '#2F855A',
  modified: '#B7791F',
  removed: '#B5483D',
  conflict: '#B7791F',
}

export type C4EdgeType = Edge<C4EdgeData, 'c4-edge'>

export const C4Edge = memo(function C4Edge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  data,
  markerEnd,
}: EdgeProps<C4EdgeType>) {
  const d = data ?? { label: null, technology: null }
  const activeLens = useCanvasStore((s) => s.activeLens)

  const [edgePath, labelX, labelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    targetX,
    targetY,
    sourcePosition,
    targetPosition,
    borderRadius: 8,
  })

  const diff = d.diffState
  const strokeColor = diff && diff !== 'unchanged' ? (diffEdgeColor[diff] ?? 'hsl(var(--border))') : 'hsl(var(--border))'
  const strokeOpacity = diff === 'removed' ? 0.5 : diff === 'unchanged' ? 0.6 : 1

  return (
    <>
      <path
        id={id}
        className="react-flow__edge-path"
        d={edgePath}
        markerEnd={markerEnd}
        style={{ stroke: strokeColor, strokeWidth: 1.5, opacity: strokeOpacity }}
      />
      {d.label && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan absolute rounded border border-border bg-card px-2 py-0.5 text-xs"
            style={{
              transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`,
              pointerEvents: 'all',
            }}
          >
            <span className="text-foreground">{d.label}</span>
            {activeLens === 'metadata' && d.technology && (
              <span className="ml-1 font-mono text-[10px] text-muted-foreground">
                {d.technology}
              </span>
            )}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  )
})
