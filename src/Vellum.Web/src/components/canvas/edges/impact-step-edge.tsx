import { memo } from 'react'
import { EdgeLabelRenderer, getBezierPath, useInternalNode, type Edge, type EdgeProps, type InternalNode } from '@xyflow/react'
import { facingEndpoints, type Rect } from '@/lib/edge-utils'
import { CERTAINTY } from '@/lib/impact'
import type { ImpactCertainty } from '@/lib/import-api'

export interface ImpactStepEdgeData extends Record<string, unknown> {
  certainty: ImpactCertainty
  /** The steps the edge stands for, with their place in the flow, such as "3. OrderCreated". */
  label: string
}

export type ImpactStepEdgeType = Edge<ImpactStepEdgeData, 'impact-step-edge'>

function nodeRect(node: InternalNode | undefined): Rect | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height }
}

/**
 * One or more affected steps of a flow between two parts on the canvas, drawn while an
 * impact is shown and never part of the model. Colour and line style say how sure the
 * impact is; the label says it in words, so the edge reads without colour.
 */
export const ImpactStepEdge = memo(function ImpactStepEdge({ id, source, target, data }: EdgeProps<ImpactStepEdgeType>) {
  const sourceRect = nodeRect(useInternalNode(source))
  const targetRect = nodeRect(useInternalNode(target))
  if (!sourceRect || !targetRect || !data) return null

  const [path, labelX, labelY] = getBezierPath(facingEndpoints(sourceRect, targetRect))
  const look = CERTAINTY[data.certainty]

  return (
    <>
      <path d={path} fill="none" stroke={look.color} strokeWidth={9} strokeOpacity={0.12} strokeLinecap="round" />
      <path
        id={id} d={path} fill="none" stroke={look.color} strokeWidth={2.25} strokeLinecap="round"
        strokeDasharray={look.dash} markerEnd={`url(#vellum-impact-arrow-${data.certainty})`}
      />
      <EdgeLabelRenderer>
        <div
          className="nodrag nopan pointer-events-none absolute z-10 max-w-60 truncate whitespace-nowrap rounded-md border bg-card px-2 py-0.5 text-[11px] text-foreground shadow-md"
          style={{ transform: `translate(-50%, -100%) translate(${labelX}px, ${labelY - 8}px)`, borderColor: look.color, borderStyle: look.line }}
        >
          {data.label} <span style={{ color: look.color }}>· {look.label}</span>
        </div>
      </EdgeLabelRenderer>
    </>
  )
})

/** Arrowheads in each certainty colour. Render once inside the canvas. */
export function ImpactStepMarkers() {
  return (
    <svg className="pointer-events-none absolute h-0 w-0" aria-hidden="true">
      <defs>
        {(Object.keys(CERTAINTY) as ImpactCertainty[]).map((certainty) => (
          <marker key={certainty} id={`vellum-impact-arrow-${certainty}`} viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill={CERTAINTY[certainty].color} />
          </marker>
        ))}
      </defs>
    </svg>
  )
}
