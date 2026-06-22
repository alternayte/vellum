import { memo } from 'react'
import {
  getSmoothStepPath,
  EdgeLabelRenderer,
  type EdgeProps,
} from '@xyflow/react'
import { useCanvasStore } from '@/stores/canvas-store'

export interface C4EdgeData {
  label: string | null
  technology: string | null
}

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
}: EdgeProps) {
  const d = (data ?? {}) as C4EdgeData
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

  return (
    <>
      <path
        id={id}
        className="react-flow__edge-path"
        d={edgePath}
        markerEnd={markerEnd}
        style={{ stroke: 'hsl(var(--border))', strokeWidth: 1.5 }}
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
