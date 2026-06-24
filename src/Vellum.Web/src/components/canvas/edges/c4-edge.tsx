import { memo, useState } from 'react'
import {
  getSmoothStepPath,
  EdgeLabelRenderer,
  type Edge,
  type EdgeProps,
} from '@xyflow/react'
import { useCanvasStore } from '@/stores/canvas-store'
import { EdgeLabelCard } from './edge-label-card'
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
  selected,
}: EdgeProps<C4EdgeType>) {
  const d = data ?? { label: null, technology: null }
  const activeLens = useCanvasStore((s) => s.activeLens)
  const [hovered, setHovered] = useState(false)

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
  const baseColor = diff && diff !== 'unchanged'
    ? (diffEdgeColor[diff] ?? 'hsl(var(--border))')
    : 'hsl(var(--border))'
  const strokeColor = selected ? 'hsl(var(--primary))' : hovered ? 'hsl(var(--foreground))' : baseColor
  const strokeWidth = hovered || selected ? 2.5 : 1.5
  const strokeOpacity = diff === 'removed' ? 0.5 : diff === 'unchanged' ? 0.6 : 1

  return (
    <>
      {/* Invisible wide path for easier hover/click target */}
      <path
        d={edgePath}
        fill="none"
        stroke="transparent"
        strokeWidth={20}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
      />
      <path
        id={id}
        className="react-flow__edge-path"
        d={edgePath}
        markerEnd={markerEnd}
        style={{
          stroke: strokeColor,
          strokeWidth,
          opacity: strokeOpacity,
          transition: 'stroke 0.15s, stroke-width 0.15s',
        }}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
      />
      {(d.label || (activeLens === 'metadata' && d.technology)) && (
        <EdgeLabelRenderer>
          <EdgeLabelCard
            label={d.label}
            technology={d.technology}
            showTechnology={activeLens === 'metadata'}
            x={labelX}
            y={labelY}
          />
        </EdgeLabelRenderer>
      )}
    </>
  )
})
