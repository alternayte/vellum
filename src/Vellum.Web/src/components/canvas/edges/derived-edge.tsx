import { memo, useState } from 'react'
import { EdgeLabelRenderer, getBezierPath, useInternalNode, type Edge, type EdgeProps, type InternalNode } from '@xyflow/react'
import { useShellStore } from '@/stores/shell-store'
import { computeEdgeOffset, relationshipEnds, type Rect } from '@/lib/edge-utils'
import { cn } from '@/lib/utils'

export interface DerivedEdgeData extends Record<string, unknown> {
  fromId: string
  toId: string
  relationshipIds: string[]
  label: string | null
  edgeIndex?: number
  totalParallel?: number
  /** The line runs from the first card of its pair, in the order of IDs, to the second. */
  forward?: boolean
}

export type DerivedEdgeType = Edge<DerivedEdgeData, 'derived-edge'>

function nodeRect(node: InternalNode | undefined): Rect | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height }
}

/**
 * A derived line: the relationships between parts hidden inside two visible cards, in one
 * direction. It is lighter than a relationship and carries a count badge. It follows the auto
 * sides and the default curve; it has no saved sides and no ends to drag.
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
  const sourceRect = nodeRect(useInternalNode(source))
  const targetRect = nodeRect(useInternalNode(target))
  const ends = sourceRect && targetRect
    ? relationshipEnds(sourceRect, targetRect, sourceOffset)
    : { sourceX: sourceX + sourceOffset, sourceY, sourcePosition, targetX: targetX + sourceOffset, targetY, targetPosition }
  const [path, centerX, centerY] = getBezierPath(ends)
  if (!data) return null

  // The label carries a badge, so parallel labels move apart along the path as in the SVG
  // export: by the index in the pair, measured in the direction of the pair, so two opposite
  // lines between the same cards move apart too.
  const labelT = totalParallel <= 1 ? 0.5 : Math.min(0.8, Math.max(0.2, 0.5 + (edgeIndex - (totalParallel - 1) / 2) * 0.3))
  const shift = (labelT - 0.5) * (data.forward === false ? -1 : 1)
  const labelX = centerX + (ends.targetX - ends.sourceX) * shift
  const labelY = centerY + (ends.targetY - ends.sourceY) * shift
  const count = data.relationshipIds.length
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
            !data.label && 'pr-0.5',
          )}
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`, pointerEvents: 'all' }}
          onMouseEnter={() => setHovered(true)}
          onMouseLeave={() => setHovered(false)}
          onClick={() => selectDerivedLine({ fromId: data.fromId, toId: data.toId, relationshipIds: data.relationshipIds })}
          aria-label={`Derived line of ${count} ${count === 1 ? 'relationship' : 'relationships'}${data.label && count === 1 ? `: ${data.label}` : ''}`}
          title="Derived line: relationships between parts inside these cards"
        >
          <span aria-hidden="true" className="flex h-4 min-w-4 items-center justify-center rounded-full bg-muted px-1 font-mono text-[10px] tabular-nums text-foreground">
            {count}
          </span>
          {data.label && <span aria-hidden="true" className="min-w-0 truncate text-xs text-muted-foreground">{data.label}</span>}
        </button>
      </EdgeLabelRenderer>
    </>
  )
})
