import { LayoutGrid, Maximize } from 'lucide-react'
import { validKindsForContext } from '@/lib/containment-rules'
import { kindColor } from '@/lib/kind-colors'

interface CanvasContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  parentKind: string | null
  onClose: () => void
  onAddElement: (kind: string) => void
  onTidy: () => void
  onZoomToFit: () => void
}

export function CanvasContextMenu({
  open,
  position,
  parentKind,
  onClose,
  onAddElement,
  onTidy,
  onZoomToFit,
}: CanvasContextMenuProps) {
  if (!open) return null

  const kinds = validKindsForContext(parentKind)

  return (
    <>
      <div className="fixed inset-0 z-40" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose() }} />
      <div
        className="fixed z-50 min-w-[180px] rounded-lg border border-border bg-popover p-1 shadow-lg"
        style={{ left: position.x, top: position.y }}
      >
        {kinds.map((kind) => (
          <button
            key={kind}
            className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent"
            onClick={() => { onAddElement(kind); onClose() }}
          >
            <span className="flex h-3.5 w-3.5 items-center justify-center">
              <span className="h-2 w-2 rounded-full" style={{ backgroundColor: kindColor(kind) }} />
            </span>
            Add {kind.charAt(0).toUpperCase() + kind.slice(1)}
          </button>
        ))}
        {kinds.length > 0 && <div className="my-1 h-px bg-border" />}
        <button className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent" onClick={() => { onTidy(); onClose() }}>
          <LayoutGrid className="h-3.5 w-3.5" /> Tidy layout
        </button>
        <button className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent" onClick={() => { onZoomToFit(); onClose() }}>
          <Maximize className="h-3.5 w-3.5" /> Zoom to fit
        </button>
      </div>
    </>
  )
}
