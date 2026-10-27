import { memo, useState } from 'react'
import {
  EdgeLabelRenderer,
  useInternalNode,
  type Edge,
  type EdgeProps,
} from '@xyflow/react'
import { useCanvasStore } from '@/stores/canvas-store'
import { cn } from '@/lib/utils'
import { EdgeLabelCard } from './edge-label-card'
import { StressRatePill } from '../stress-overlay'
import { useLineRate } from '../stress-overlay-context'
import { computeEdgeOffset, computeLabelOffset, getEdgePathFn, labelZ, nodeBoxes, relationshipEnds, routeOnCanvas, type LayoutRoute } from '@/lib/edge-utils'
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
  /** The saved side of each end in this view: top, right, bottom or left. Null is auto. */
  sourceSide?: string | null
  targetSide?: string | null
  /** The route of the auto layout, when one applies (routeApplies). It holds while both cards stay. */
  route?: LayoutRoute | null
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
  const rate = useLineRate(id)

  const edgeIndex = d.edgeIndex ?? 0
  const totalParallel = d.totalParallel ?? 1
  const { sourceOffset } = computeEdgeOffset(edgeIndex, totalParallel)

  // A saved route of the auto layout draws while both cards keep the boxes it was laid out for.
  // Otherwise the path attaches to the saved side of each end, or to the side that faces the
  // other card for an auto end, from the live boxes of the cards. Fall back to the handle
  // coordinates until both nodes are measured.
  const sourceBoxes = nodeBoxes(useInternalNode(source))
  const targetBoxes = nodeBoxes(useInternalNode(target))
  const routed = routeOnCanvas(d.route, sourceBoxes, targetBoxes)
  const ends = sourceBoxes && targetBoxes
    ? relationshipEnds(sourceBoxes.absolute, targetBoxes.absolute, sourceOffset, { sourceSide: d.sourceSide, targetSide: d.targetSide })
    : { sourceX: sourceX + sourceOffset, sourceY, sourcePosition, targetX: targetX + sourceOffset, targetY, targetPosition }

  const pathFn = getEdgePathFn(d.lineShape)
  const [autoPath, defaultLabelX, defaultLabelY] = pathFn(ends)

  // Offset label position along the edge for parallel edges so labels don't stack. A routed
  // line's label sits where the layout made room for it.
  const labelT = computeLabelOffset(edgeIndex, totalParallel)
  const edgePath = routed?.path ?? autoPath
  const labelX = routed?.labelX ?? defaultLabelX + (ends.targetX - ends.sourceX) * (labelT - 0.5) * 0.3
  const labelY = routed?.labelY ?? defaultLabelY + (ends.targetY - ends.sourceY) * (labelT - 0.5) * 0.3

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
            zIndex={labelZ(sourceBoxes, targetBoxes)}
          />
        </EdgeLabelRenderer>
      )}
      {rate !== null && (
        <EdgeLabelRenderer>
          <StressRatePill rate={rate} x={labelX} y={labelY} zIndex={labelZ(sourceBoxes, targetBoxes)} />
        </EdgeLabelRenderer>
      )}
    </>
  )
})
