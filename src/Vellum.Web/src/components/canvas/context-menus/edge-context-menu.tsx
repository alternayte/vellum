import { ArrowLeftRight, Pencil, Trash2 } from 'lucide-react'

interface EdgeContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  onClose: () => void
  onEditDetails: () => void
  onReverse: () => void
  onDelete: () => void
}

export function EdgeContextMenu({
  open,
  position,
  onClose,
  onEditDetails,
  onReverse,
  onDelete,
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
