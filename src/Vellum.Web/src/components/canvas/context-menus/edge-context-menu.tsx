import { ArrowLeftRight, Pencil, Trash2, Spline, Minus, CornerDownRight } from 'lucide-react'
import { ContextMenu, MenuGroup, MenuItem, MenuSeparator } from './menu'

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
  const shape = currentLineShape || 'bezier'

  return (
    <ContextMenu position={position} label="Relationship" onClose={onClose}>
      <MenuItem icon={<Pencil className="h-3.5 w-3.5" />} label="Edit details" onSelect={() => { onEditDetails(); onClose() }} />
      {onLineShapeChange && (
        <>
          <MenuSeparator />
          <MenuGroup label="Line style">
            <MenuItem icon={<Spline className="h-3.5 w-3.5" />} label="Curved" checked={shape === 'bezier'} onSelect={() => { onLineShapeChange('bezier'); onClose() }} />
            <MenuItem icon={<Minus className="h-3.5 w-3.5" />} label="Straight" checked={shape === 'straight'} onSelect={() => { onLineShapeChange('straight'); onClose() }} />
            <MenuItem icon={<CornerDownRight className="h-3.5 w-3.5" />} label="Right-angle" checked={shape === 'step'} onSelect={() => { onLineShapeChange('step'); onClose() }} />
          </MenuGroup>
        </>
      )}
      <MenuSeparator />
      <MenuItem icon={<ArrowLeftRight className="h-3.5 w-3.5" />} label="Reverse direction" onSelect={() => { onReverse(); onClose() }} />
      <MenuSeparator />
      <MenuItem icon={<Trash2 className="h-3.5 w-3.5" />} label="Delete" destructive onSelect={() => { onDelete(); onClose() }} />
    </ContextMenu>
  )
}
