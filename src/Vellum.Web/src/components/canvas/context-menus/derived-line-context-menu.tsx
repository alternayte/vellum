import { EyeOff, List, Waypoints } from 'lucide-react'
import { ContextMenu, MenuItem, MenuSeparator } from './menu'

interface DerivedLineContextMenuProps {
  position: { x: number; y: number }
  onClose: () => void
  /** Opens the list of the line's relationships in the detail panel. */
  onListRelationships: () => void
  /** Hides this line in the open view, by its two cards. Absent when the view cannot change. */
  onHideLine?: () => void
  /** Turns derived lines off in the open view. Absent when the view cannot change. */
  onHideAllDerived?: () => void
}

/** The menu of a derived line: a line with no ID of its own, drawn for relationships inside its two cards. */
export function DerivedLineContextMenu({ position, onClose, onListRelationships, onHideLine, onHideAllDerived }: DerivedLineContextMenuProps) {
  return (
    <ContextMenu position={position} label="Derived line" onClose={onClose}>
      <MenuItem icon={<List className="h-3.5 w-3.5" />} label="List its relationships" onSelect={() => { onListRelationships(); onClose() }} />
      {(onHideLine || onHideAllDerived) && <MenuSeparator />}
      {onHideLine && <MenuItem icon={<EyeOff className="h-3.5 w-3.5" />} label="Hide this line" onSelect={() => { onHideLine(); onClose() }} />}
      {onHideAllDerived && (
        <MenuItem icon={<Waypoints className="h-3.5 w-3.5" />} label="Hide all derived lines" onSelect={() => { onHideAllDerived(); onClose() }} />
      )}
    </ContextMenu>
  )
}
