import {
  ChevronRight,
  Copy,
  Maximize2,
  Minimize2,
  Pencil,
  Plus,
  Trash2,
} from 'lucide-react'
import { ContextMenu, MenuItem, MenuSeparator } from './menu'

interface NodeContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  hasChildren: boolean
  isExpanded: boolean
  /** The kinds this element can hold. Empty for an element that holds none. */
  childKinds: string[]
  /** False for a node that is shown inside an expanded parent: it expands on its own level only. */
  canExpand: boolean
  onAddChild: (kind: string) => void
  onClose: () => void
  onEditDetails: () => void
  onExpand: () => void
  onDrillInto: () => void
  onDuplicate: () => void
  onDelete: () => void
  /** The model cannot be changed: the menu shows what looks and leaves out what writes. */
  readOnly?: boolean
}

export function NodeContextMenu({
  open,
  position,
  hasChildren,
  isExpanded,
  childKinds,
  canExpand,
  onAddChild,
  onClose,
  onEditDetails,
  onExpand,
  onDrillInto,
  onDuplicate,
  onDelete,
  readOnly = false,
}: NodeContextMenuProps) {
  if (!open) return null

  return (
    <ContextMenu position={position} label="Element" onClose={onClose}>
      <MenuItem icon={<Pencil className="h-3.5 w-3.5" />} label={readOnly ? 'Show details' : 'Edit details'} onSelect={() => { onEditDetails(); onClose() }} />
      {childKinds.length > 0 && (
        <>
          <MenuSeparator />
          {!readOnly && childKinds.map((kind) => (
            <MenuItem
              key={kind}
              icon={<Plus className="h-3.5 w-3.5" />}
              label={`Add ${kind} inside`}
              onSelect={() => { onAddChild(kind); onClose() }}
            />
          ))}
          {hasChildren && canExpand && (
            <MenuItem
              icon={isExpanded ? <Minimize2 className="h-3.5 w-3.5" /> : <Maximize2 className="h-3.5 w-3.5" />}
              label={isExpanded ? 'Collapse' : 'Expand'}
              onSelect={() => { onExpand(); onClose() }}
            />
          )}
          <MenuItem
            icon={<ChevronRight className="h-3.5 w-3.5" />}
            label="Drill into"
            onSelect={() => { onDrillInto(); onClose() }}
          />
        </>
      )}
      {!readOnly && (
        <>
          <MenuSeparator />
          <MenuItem icon={<Copy className="h-3.5 w-3.5" />} label="Duplicate" onSelect={() => { onDuplicate(); onClose() }} />
          <MenuSeparator />
          <MenuItem icon={<Trash2 className="h-3.5 w-3.5" />} label="Delete" onSelect={() => { onDelete(); onClose() }} destructive />
        </>
      )}
    </ContextMenu>
  )
}
