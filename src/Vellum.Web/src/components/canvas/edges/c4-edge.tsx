import { memo, useState } from 'react'
import {
  EdgeLabelRenderer,
  useInternalNode,
  type Edge,
  type InternalNode,
  type EdgeProps,
} from '@xyflow/react'
import { useCanvasStore } from '@/stores/canvas-store'
import { cn } from '@/lib/utils'
import { EdgeLabelCard } from './edge-label-card'
import { computeEdgeOffset, computeLabelOffset, facingEndpoints, getEdgePathFn, type Rect } from '@/lib/edge-utils'
import type { DiffState } from '@/stores/draft-store'

export interface C4EdgeData extends Record<string, unknown> {
  label: string | null
  technology: string | null
  lineShape?: string | null
  relationshipKind?: string | null
  factStatus?: string | null
  diffState?: DiffState
  edgeIndex?: number
  totalParallel?: number
  /** A trace event runs along this relationship now; its label goes below the path. */
  labelBelow?: boolean
}

const diffEdgeColor: Record<string, string> = {
  added: '#2F855A',
  modified: '#B7791F',
  removed: '#B5483D',
  conflict: '#B7791F',
}

export type C4EdgeType = Edge<C4EdgeData, 'c4-edge'>

function nodeRect(node: InternalNode | undefined): Rect | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height }
}

export const C4Edge = memo(function C4Edge({
  id,
  source,
  target,
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

  const edgeIndex = d.edgeIndex ?? 0
  const totalParallel = d.totalParallel ?? 1
  const { sourceOffset } = computeEdgeOffset(edgeIndex, totalParallel)

  // Handles are fixed on the node sides; the drawn path instead attaches to the sides that
  // face each other. Fall back to the handle coordinates until both nodes are measured.
  const sourceRect = nodeRect(useInternalNode(source))
  const targetRect = nodeRect(useInternalNode(target))
  const ends = sourceRect && targetRect
    ? facingEndpoints(sourceRect, targetRect, sourceOffset)
    : { sourceX: sourceX + sourceOffset, sourceY, sourcePosition, targetX: targetX + sourceOffset, targetY, targetPosition }

  const pathFn = getEdgePathFn(d.lineShape)
  const [edgePath, defaultLabelX, defaultLabelY] = pathFn(ends)

  // Offset label position along the edge for parallel edges so labels don't stack
  const labelT = computeLabelOffset(edgeIndex, totalParallel)
  const labelX = defaultLabelX + (ends.targetX - ends.sourceX) * (labelT - 0.5) * 0.3
  const labelY = defaultLabelY + (ends.targetY - ends.sourceY) * (labelT - 0.5) * 0.3

  const diff = d.diffState
  const isBehavioural = d.relationshipKind === 'behavioural'
  const baseColor = diff && diff !== 'unchanged'
    ? (diffEdgeColor[diff] ?? 'var(--muted-foreground)')
    : isBehavioural
      ? '#2dd4bf'
      : 'var(--muted-foreground)'
  const strokeColor = selected ? 'var(--primary)' : hovered ? 'var(--foreground)' : baseColor
  const strokeWidth = hovered || selected ? 2.8 : isBehavioural ? 2 : 1.5
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
        className={cn(
          'react-flow__edge-path',
          isBehavioural && 'vellum-edge-flow',
        )}
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
            below={d.labelBelow}
          />
        </EdgeLabelRenderer>
      )}
    </>
  )
})
