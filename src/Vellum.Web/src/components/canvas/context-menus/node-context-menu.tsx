import {
  ChevronRight,
  Copy,
  Maximize2,
  Minimize2,
  Pencil,
  Trash2,
} from 'lucide-react'

interface NodeContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  hasChildren: boolean
  isExpanded: boolean
  onClose: () => void
  onEditDetails: () => void
  onExpand: () => void
  onDrillInto: () => void
  onDuplicate: () => void
  onDelete: () => void
}

export function NodeContextMenu({
  open,
  position,
  hasChildren,
  isExpanded,
  onClose,
  onEditDetails,
  onExpand,
  onDrillInto,
  onDuplicate,
  onDelete,
}: NodeContextMenuProps) {
  if (!open) return null

  return (
    <>
      <div className="fixed inset-0 z-40" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose() }} />
      <div
        className="fixed z-50 min-w-[180px] rounded-lg border border-border bg-popover p-1 shadow-lg"
        style={{ left: position.x, top: position.y }}
      >
        <MenuItem icon={<Pencil className="h-3.5 w-3.5" />} label="Edit details" onClick={() => { onEditDetails(); onClose() }} />
        {hasChildren && (
          <>
            <div className="my-1 h-px bg-border" />
            <MenuItem
              icon={isExpanded ? <Minimize2 className="h-3.5 w-3.5" /> : <Maximize2 className="h-3.5 w-3.5" />}
              label={isExpanded ? 'Collapse' : 'Expand'}
              onClick={() => { onExpand(); onClose() }}
            />
            <MenuItem
              icon={<ChevronRight className="h-3.5 w-3.5" />}
              label="Drill into"
              onClick={() => { onDrillInto(); onClose() }}
            />
          </>
        )}
        <div className="my-1 h-px bg-border" />
        <MenuItem icon={<Copy className="h-3.5 w-3.5" />} label="Duplicate" onClick={() => { onDuplicate(); onClose() }} />
        <div className="my-1 h-px bg-border" />
        <MenuItem icon={<Trash2 className="h-3.5 w-3.5" />} label="Delete" onClick={() => { onDelete(); onClose() }} destructive />
      </div>
    </>
  )
}

function MenuItem({ icon, label, onClick, destructive }: { icon: React.ReactNode; label: string; onClick: () => void; destructive?: boolean }) {
  return (
    <button
      className={`flex w-full items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-accent ${destructive ? 'text-destructive' : 'text-popover-foreground'}`}
      onClick={onClick}
    >
      {icon}
      {label}
    </button>
  )
}
