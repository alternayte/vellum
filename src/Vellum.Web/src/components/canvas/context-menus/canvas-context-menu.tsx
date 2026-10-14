import { LayoutGrid, Maximize } from 'lucide-react'
import { validKindsForContext } from '@/lib/containment-rules'
import { kindColor } from '@/lib/kind-colors'
import { ContextMenu, MenuItem, MenuSeparator } from './menu'

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
    <ContextMenu position={position} label="Canvas" onClose={onClose}>
      {kinds.map((kind) => (
        <MenuItem
          key={kind}
          icon={<span className="h-2 w-2 rounded-full" style={{ backgroundColor: kindColor(kind) }} />}
          label={`Add ${kind.charAt(0).toUpperCase() + kind.slice(1)}`}
          onSelect={() => { onAddElement(kind); onClose() }}
        />
      ))}
      {kinds.length > 0 && <MenuSeparator />}
      <MenuItem icon={<LayoutGrid className="h-3.5 w-3.5" />} label="Tidy layout" onSelect={() => { onTidy(); onClose() }} />
      <MenuItem icon={<Maximize className="h-3.5 w-3.5" />} label="Zoom to fit" onSelect={() => { onZoomToFit(); onClose() }} />
    </ContextMenu>
  )
}
