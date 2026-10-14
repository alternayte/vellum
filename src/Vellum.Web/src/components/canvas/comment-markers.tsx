import { memo } from 'react'
import { ViewportPortal, useStore, type InternalNode } from '@xyflow/react'
import { MessageSquare } from 'lucide-react'
import { useShellStore } from '@/stores/shell-store'

/** The open threads of one target: how many, and the first one, which a click brings into view. */
export interface CommentMarker {
  count: number
  threadId: string
}

interface Point {
  x: number
  y: number
}

function corner(node: InternalNode | undefined): Point | null {
  const width = node?.measured.width
  if (!node || !width || !node.measured.height) return null
  return { x: node.internals.positionAbsolute.x + width - 6, y: node.internals.positionAbsolute.y - 10 }
}

function centre(node: InternalNode | undefined): Point | null {
  const width = node?.measured.width
  const height = node?.measured.height
  if (!node || !width || !height) return null
  return { x: node.internals.positionAbsolute.x + width / 2, y: node.internals.positionAbsolute.y + height / 2 }
}

/**
 * A badge on each element, message and relationship of the canvas that has open comment
 * threads. It is an overlay derived from the thread list and the measured nodes: the nodes and
 * edges carry no comment data. A target that is not on this level of the canvas gets no badge.
 */
export const CommentMarkers = memo(function CommentMarkers({ markers }: { markers: Record<string, CommentMarker> }) {
  const nodeLookup = useStore((s) => s.nodeLookup)
  const edges = useStore((s) => s.edges)
  // The badge keeps one size on screen at every zoom, up to a limit when far out.
  const zoom = useStore((s) => s.transform[2])

  const placed: { id: string; at: Point; marker: CommentMarker; open: () => void; label: string }[] = []
  for (const [id, marker] of Object.entries(markers)) {
    const element = nodeLookup.get(id)
    const message = nodeLookup.get(`msg-${id}`)
    const edge = edges.find((e) => e.id === id)
    const at = element ? corner(element)
      : message ? corner(message)
      : edge ? midpoint(centre(nodeLookup.get(edge.source)), centre(nodeLookup.get(edge.target)))
      : null
    if (!at) continue
    const kind = element ? 'element' : message ? 'message' : 'relationship'
    placed.push({
      id, at, marker, label: `${marker.count} open comment thread${marker.count === 1 ? '' : 's'} on this ${kind}`,
      open: () => {
        const { selectElement, selectRelationship, selectMessage, focusThread } = useShellStore.getState()
        ;(element ? selectElement : message ? selectMessage : selectRelationship)(id)
        focusThread(marker.threadId)
      },
    })
  }

  if (placed.length === 0) return null
  const scale = Math.min(1 / zoom, 2.5)
  return (
    <ViewportPortal>
      {placed.map(({ id, at, marker, open, label }) => (
        <button
          key={id}
          type="button"
          aria-label={label}
          title={label}
          onClick={(e) => { e.stopPropagation(); open() }}
          className="nodrag nopan absolute flex h-5 min-w-5 items-center justify-center gap-0.5 rounded-full border border-primary/60 bg-card px-1 text-[10px] font-medium tabular-nums text-primary shadow-sm hover:bg-primary hover:text-primary-foreground focus-visible:outline-2 focus-visible:outline-primary"
          style={{
            transform: `translate(${at.x}px, ${at.y}px) translate(-50%, -50%) scale(${scale})`,
            pointerEvents: 'all',
            zIndex: 10,
          }}
        >
          <MessageSquare className="h-2.5 w-2.5" aria-hidden="true" />
          {marker.count}
        </button>
      ))}
    </ViewportPortal>
  )
})

function midpoint(a: Point | null, b: Point | null): Point | null {
  return a && b ? { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } : null
}
