import { memo, useState } from 'react'
import { EdgeLabelRenderer, getBezierPath, useInternalNode, type Edge, type EdgeProps, type InternalNode } from '@xyflow/react'
import { useShellStore } from '@/stores/shell-store'
import { relationshipEnds, type Rect } from '@/lib/edge-utils'
import { cn } from '@/lib/utils'
import type { GhostNodeData } from '@/lib/ghost-layer'
import { GhostActions } from '../nodes/ghost-node'

export interface GhostEdgeData extends GhostNodeData {
  label: string | null
  behavioural: boolean
}

export type GhostEdgeType = Edge<GhostEdgeData, 'ghost-edge'>

function nodeRect(node: InternalNode | undefined): Rect | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x, y: node.internals.positionAbsolute.y, width, height }
}

/** A proposed relationship that is not in the model: a dashed line, with its label, accept and reject on a card. */
export const GhostEdge = memo(function GhostEdge({ id, source, target, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data, markerEnd }: EdgeProps<GhostEdgeType>) {
  const [hovered, setHovered] = useState(false)
  const selectGhost = useShellStore((s) => s.selectGhost)
  const selected = useShellStore((s) => !!data && s.selectedGhostItemId === data.itemId)
  const sourceRect = nodeRect(useInternalNode(source))
  const targetRect = nodeRect(useInternalNode(target))
  const ends = sourceRect && targetRect
    ? relationshipEnds(sourceRect, targetRect)
    : { sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition }
  const [path, labelX, labelY] = getBezierPath(ends)
  if (!data) return null
  const refused = data.refused.length > 0
  const stroke = refused ? 'var(--destructive)' : selected ? 'var(--primary)' : hovered ? 'var(--foreground)' : data.behavioural ? '#2dd4bf' : 'var(--muted-foreground)'

  return (
    <>
      <path d={path} fill="none" stroke="transparent" strokeWidth={20} onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)} />
      <path
        id={id}
        className="react-flow__edge-path"
        d={path}
        markerEnd={markerEnd}
        style={{ stroke, strokeWidth: selected || hovered ? 2.4 : 1.6, strokeDasharray: '7 5', opacity: 0.9 }}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
      />
      <EdgeLabelRenderer>
        <div
          className={cn(
            'group/ghost nodrag nopan absolute flex max-w-56 items-center gap-1.5 rounded-md border border-dashed bg-card/90 px-2 py-1 shadow-sm backdrop-blur-sm',
            refused ? 'border-destructive' : selected ? 'border-primary' : 'border-border',
          )}
          style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`, pointerEvents: 'all' }}
          onMouseEnter={() => setHovered(true)}
          onMouseLeave={() => setHovered(false)}
          onClick={() => selectGhost(data.itemId)}
        >
          <span className="min-w-0 cursor-pointer">
            <span className="block truncate text-xs text-foreground/85">{data.label ?? data.name}</span>
            <span className="block font-mono text-[10px] text-[var(--trace-retry)]">proposed · {data.alias} · {Math.round(data.confidence * 100)}%</span>
            {refused && <span className="block text-[10px] leading-snug text-destructive">Refused: {data.refused.join(' ')}</span>}
            {data.issues.length > 0 && <span className="block text-[10px] leading-snug text-[var(--trace-retry)]">Cannot be added now: {data.issues.join(' ')}</span>}
          </span>
          <GhostActions data={data} visible={selected || hovered} />
        </div>
      </EdgeLabelRenderer>
    </>
  )
})
