import { LayoutGrid, Maximize } from 'lucide-react'
import { validKindsForContext } from '@/lib/containment-rules'
import { kindColor } from '@/lib/kind-colors'
import { ContextMenu, MenuCheckItem, MenuGroup, MenuItem, MenuSeparator } from './menu'
import { LINE_SWITCHES, withSwitch } from '@/lib/line-visibility'
import type { LineControls } from '../line-controls'

interface CanvasContextMenuProps {
  open: boolean
  position: { x: number; y: number }
  parentKind: string | null
  onClose: () => void
  onAddElement: (kind: string) => void
  onTidy: () => void
  onZoomToFit: () => void
  /** The line controls of the open view. Without them the menu has no Lines group. */
  lines?: LineControls
}

export function CanvasContextMenu({
  open,
  position,
  parentKind,
  onClose,
  onAddElement,
  onTidy,
  onZoomToFit,
  lines,
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
      {lines && (
        <>
          <MenuSeparator />
          <MenuGroup label="Lines">
            {lines.onChange && LINE_SWITCHES.map(({ key, label }) => (
              <MenuCheckItem
                key={key}
                label={label}
                checked={lines.visibility[key]}
                onToggle={(shown) => lines.onChange?.(withSwitch(lines.visibility, key, shown))}
              />
            ))}
            <MenuCheckItem label="Show hidden lines" checked={lines.showHidden} onToggle={lines.onShowHidden} />
          </MenuGroup>
        </>
      )}
    </ContextMenu>
  )
}
