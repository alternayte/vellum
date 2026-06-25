import { ArrowLeftRight, Pencil, Trash2, Spline, Minus, CornerDownRight } from 'lucide-react'
import { cn } from '@/lib/utils'

interface EdgeContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  currentLineShape?: string | null
  onClose: () => void
  onEditDetails: () => void
  onReverse: () => void
  onDelete: () => void
  onLineShapeChange?: (shape: string) => void
}

export function EdgeContextMenu({
  open,
  position,
  currentLineShape,
  onClose,
  onEditDetails,
  onReverse,
  onDelete,
  onLineShapeChange,
}: EdgeContextMenuProps) {
  if (!open) return null

  return (
    <>
      <div className="fixed inset-0 z-40" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose() }} />
      <div
        className="fixed z-50 min-w-[180px] rounded-lg border border-border bg-popover p-1 shadow-lg"
        style={{ left: position.x, top: position.y }}
      >
        <button className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent" onClick={() => { onEditDetails(); onClose() }}>
          <Pencil className="h-3.5 w-3.5" /> Edit details
        </button>
        {onLineShapeChange && (
          <>
            <div className="my-1 h-px bg-border" />
            <div className="px-2 py-1 text-[10px] uppercase tracking-wider text-muted-foreground">Line style</div>
            <button className={cn("flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent", (!currentLineShape || currentLineShape === 'bezier') && "bg-accent")} onClick={() => { onLineShapeChange('bezier'); onClose() }}>
              <Spline className="h-3.5 w-3.5" /> Curved
            </button>
            <button className={cn("flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent", currentLineShape === 'straight' && "bg-accent")} onClick={() => { onLineShapeChange('straight'); onClose() }}>
              <Minus className="h-3.5 w-3.5" /> Straight
            </button>
            <button className={cn("flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent", currentLineShape === 'step' && "bg-accent")} onClick={() => { onLineShapeChange('step'); onClose() }}>
              <CornerDownRight className="h-3.5 w-3.5" /> Right-angle
            </button>
          </>
        )}
        <div className="my-1 h-px bg-border" />
        <button className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-popover-foreground hover:bg-accent" onClick={() => { onReverse(); onClose() }}>
          <ArrowLeftRight className="h-3.5 w-3.5" /> Reverse direction
        </button>
        <div className="my-1 h-px bg-border" />
        <button className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm text-destructive hover:bg-accent" onClick={() => { onDelete(); onClose() }}>
          <Trash2 className="h-3.5 w-3.5" /> Delete
        </button>
      </div>
    </>
  )
}
