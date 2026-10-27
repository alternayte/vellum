import { memo, useState } from 'react'
import { EdgeLabelRenderer, getBezierPath, useInternalNode, type Edge, type EdgeProps } from '@xyflow/react'
import { useShellStore } from '@/stores/shell-store'
import { computeEdgeOffset, labelZ, nodeBoxes, relationshipEnds, routeOnCanvas, type LayoutRoute } from '@/lib/edge-utils'
import { cn } from '@/lib/utils'

export interface DerivedEdgeData extends Record<string, unknown> {
  fromId: string
  toId: string
  relationshipIds: string[]
  /** How many relationships under the line a share link does not share. */
  hiddenCount?: number
  label: string | null
  edgeIndex?: number
  totalParallel?: number
  /** The line runs from the first card of its pair, in the order of IDs, to the second. */
  forward?: boolean
  /** The route of the auto layout. It holds while both cards stay. */
  route?: LayoutRoute | null
  /** The view hides this line; it is drawn faint because the person asked to see hidden lines. */
  hiddenInView?: boolean
}

export type DerivedEdgeType = Edge<DerivedEdgeData, 'derived-edge'>

/**
 * A derived line: the relationships between parts hidden inside two visible cards, in one
 * direction. It is lighter than a relationship and carries a count badge when it holds more than one relationship. It takes the route of
 * the auto layout while its cards stay, and otherwise the auto sides and the default curve; it
 * has no saved sides and no ends to drag.
 */
export const DerivedEdge = memo(function DerivedEdge({
  id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data, markerEnd,
}: EdgeProps<DerivedEdgeType>) {
  const [hovered, setHovered] = useState(false)
  const selectDerivedLine = useShellStore((s) => s.selectDerivedLine)
  // Selected while the detail panel lists it, from a click on the line or on its label.
  const selected = useShellStore((s) => !!data && s.selectedDerivedLine?.fromId === data.fromId && s.selectedDerivedLine.toId === data.toId)
  const edgeIndex = data?.edgeIndex ?? 0
  const totalParallel = data?.totalParallel ?? 1
  const { sourceOffset } = computeEdgeOffset(edgeIndex, totalParallel)
  const sourceBoxes = nodeBoxes(useInternalNode(source))
  const targetBoxes = nodeBoxes(useInternalNode(target))
  const routed = routeOnCanvas(data?.route, sourceBoxes, targetBoxes)
  const ends = sourceBoxes && targetBoxes
    ? relationshipEnds(sourceBoxes.absolute, targetBoxes.absolute, sourceOffset)
    : { sourceX: sourceX + sourceOffset, sourceY, sourcePosition, targetX: targetX + sourceOffset, targetY, targetPosition }
  const [autoPath, centerX, centerY] = getBezierPath(ends)
  if (!data) return null

  // A routed line takes the route of the auto layout and its label point. Otherwise the label
  // carries a badge, so parallel labels move apart along the path as in the SVG export: by the
  // index in the pair, measured in the direction of the pair, so two opposite lines between the
  // same cards move apart too.
  const labelT = totalParallel <= 1 ? 0.5 : Math.min(0.8, Math.max(0.2, 0.5 + (edgeIndex - (totalParallel - 1) / 2) * 0.3))
  const shift = (labelT - 0.5) * (data.forward === false ? -1 : 1)
  const path = routed?.path ?? autoPath
  const labelX = routed?.labelX ?? centerX + (ends.targetX - ends.sourceX) * shift
  const labelY = routed?.labelY ?? centerY + (ends.targetY - ends.sourceY) * shift
  const count = data.relationshipIds.length + (data.hiddenCount ?? 0)
  const stroke = selected ? 'var(--primary)' : hovered ? 'var(--foreground)' : 'var(--muted-foreground)'

  return (
    <>
      <path d={path} fill="none" stroke="transparent" strokeWidth={20} onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)} />
      <path
        id={id}
        className="react-flow__edge-path"
        d={path}
        markerEnd={markerEnd}
        style={{ stroke, strokeWidth: selected || hovered ? 1.8 : 1, opacity: selected || hovered ? 1 : 0.7, transition: 'stroke 0.15s, stroke-width 0.15s' }}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
      />
      <EdgeLabelRenderer>
        <button
          type="button"
          className={cn(
            'nodrag nopan absolute flex max-w-56 items-center gap-1.5 rounded-full border border-dashed bg-card/95 py-0.5 pl-0.5 pr-2 shadow-sm backdrop-blur-sm focus-visible:outline-2 focus-visible:outline-primary',
            selected ? 'border-primary' : 'border-border',
            count === 1 && 'pl-2',
            data.hiddenInView && 'opacity-40',
          )}
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`, pointerEvents: 'all', zIndex: labelZ(sourceBoxes, targetBoxes) }}
          onMouseEnter={() => setHovered(true)}
          onMouseLeave={() => setHovered(false)}
          onClick={() => selectDerivedLine({ fromId: data.fromId, toId: data.toId, relationshipIds: data.relationshipIds, hiddenCount: data.hiddenCount ?? 0, label: data.label })}
          aria-label={`Derived line of ${count} ${count === 1 ? 'relationship' : 'relationships'}${data.label && count === 1 ? `: ${data.label}` : ''}`}
          title="Derived line: relationships between parts inside these cards"
        >
          {count > 1 && (
            <span aria-hidden="true" className="flex h-4 min-w-4 items-center justify-center rounded-full bg-muted px-1 font-mono text-[10px] tabular-nums text-foreground">
              {count}
            </span>
          )}
          {/* The badge carries the count of a merged line, so it reads "3 relationships", not "3 3 relationships". */}
          <span aria-hidden="true" className="min-w-0 truncate text-xs text-muted-foreground">
            {count === 1 ? (data.label ?? 'relationship') : 'relationships'}
          </span>
        </button>
      </EdgeLabelRenderer>
    </>
  )
})
