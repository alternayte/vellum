import { memo } from 'react'
import { EdgeLabelRenderer, getBezierPath, useInternalNode, type Edge, type EdgeProps, type InternalNode } from '@xyflow/react'
import { facingEndpoints, type Rect } from '@/lib/edge-utils'
import type { TraceFamily } from '@/lib/trace'

export interface TraceEdgeData extends Record<string, unknown> {
  family: TraceFamily
  label: string
  /** Seconds one particle takes to cross the edge; already scaled by playback speed. */
  travelSeconds: number
}

export type TraceEdgeType = Edge<TraceEdgeData, 'trace-edge'>

function nodeRect(node: InternalNode | undefined): Rect | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height }
}

/**
 * The interaction a trace is showing right now. It exists only while playback is on that
 * event and is never part of the model. The label states what moves, so the edge reads the
 * same with motion off; the particle only adds direction.
 */
export const TraceEdge = memo(function TraceEdge({ id, source, target, data }: EdgeProps<TraceEdgeType>) {
  const sourceRect = nodeRect(useInternalNode(source))
  const targetRect = nodeRect(useInternalNode(target))
  if (!sourceRect || !targetRect || !data) return null

  const [path, labelX, labelY] = getBezierPath(facingEndpoints(sourceRect, targetRect))
  const color = `var(--trace-${data.family})`
  const failed = data.family === 'failure'

  return (
    <>
      <path d={path} fill="none" stroke={color} strokeWidth={10} strokeOpacity={0.14} strokeLinecap="round" />
      <path
        id={id}
        d={path}
        fill="none"
        stroke={color}
        strokeWidth={2.5}
        strokeLinecap="round"
        strokeDasharray={failed ? '2 7' : data.family === 'retry' ? '8 6' : undefined}
        markerEnd={`url(#vellum-trace-arrow-${data.family})`}
      />
      {!failed && (
        <circle r={4.5} fill={color} className="vellum-trace-particle">
          <animateMotion dur={`${data.travelSeconds}s`} repeatCount="indefinite" path={path} />
        </circle>
      )}
      <EdgeLabelRenderer>
        <div
          className="nodrag nopan pointer-events-none absolute z-10 max-w-56 whitespace-nowrap rounded-md border bg-card px-2 py-1 text-xs font-medium text-foreground shadow-lg"
          // Sits above the path so a short edge and its arrow stay visible.
          style={{ transform: `translate(-50%, -100%) translate(${labelX}px, ${labelY - 10}px)`, borderColor: color }}
        >
          {data.label}
        </div>
      </EdgeLabelRenderer>
    </>
  )
})

const FAMILIES: TraceFamily[] = ['request', 'event', 'data', 'retry', 'failure', 'control']

/** Arrowheads in each trace colour. Render once inside the canvas. */
export function TraceEdgeMarkers() {
  return (
    <svg className="pointer-events-none absolute h-0 w-0" aria-hidden="true">
      <defs>
        {FAMILIES.map((family) => (
          <marker key={family} id={`vellum-trace-arrow-${family}`} viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill={`var(--trace-${family})`} />
          </marker>
        ))}
      </defs>
    </svg>
  )
}
