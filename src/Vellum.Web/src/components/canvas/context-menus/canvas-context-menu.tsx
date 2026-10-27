import { BoxSelect, ClipboardPaste, Eye, FileCode, FileImage, Image, LayoutGrid, Link, Maximize, Network } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'
import { ContextMenu, MenuCheckItem, MenuGroup, MenuItem, MenuSeparator } from './menu'
import { LINE_SWITCHES, withSwitch } from '@/lib/line-visibility'
import type { LineControls } from '../line-controls'

export type ViewExport = 'svg' | 'png' | 'mermaid' | 'dsl'

interface CanvasContextMenuProps {
  position: { x: number; y: number }
  /** The kinds a person can add at the pointer. Empty when the model cannot be changed here. */
  addKinds: string[]
  onClose: () => void
  onAddElement: (kind: string) => void
  /** What a paste adds, such as "Paste Orders API", or null when nothing copied fits here. */
  pasteLabel: string | null
  onPaste: () => void
  onSelectAll: () => void
  onTidy?: () => void
  onZoomToFit: () => void
  /** The line controls of the open view. Without them the menu has no Lines group. */
  lines?: LineControls
  /** The cards of this level that a person hid from the view. */
  hiddenCardCount: number
  onShowHiddenCards?: () => void
  onExport?: (format: ViewExport) => void
  onCopyLink?: () => void
}

const icon = 'h-3.5 w-3.5'

/**
 * The menu of the empty canvas. Each group is one kind of action: add, edit, arrange, the
 * cards of the view, export and share. A group that has no action here is left out.
 */
export function CanvasContextMenu({
  position,
  addKinds,
  onClose,
  onAddElement,
  pasteLabel,
  onPaste,
  onSelectAll,
  onTidy,
  onZoomToFit,
  lines,
  hiddenCardCount,
  onShowHiddenCards,
  onExport,
  onCopyLink,
}: CanvasContextMenuProps) {
  const run = (action: () => void) => () => { action(); onClose() }

  return (
    <ContextMenu position={position} label="Canvas" onClose={onClose}>
      {addKinds.length > 0 && (
        <>
          <MenuGroup label="Add here">
            {addKinds.map((kind) => (
              <MenuItem
                key={kind}
                icon={<span className="h-2 w-2 rounded-full" style={{ backgroundColor: kindColor(kind) }} />}
                label={`Add ${kind}`}
                onSelect={run(() => onAddElement(kind))}
              />
            ))}
          </MenuGroup>
          <MenuSeparator />
        </>
      )}
      {pasteLabel && <MenuItem icon={<ClipboardPaste className={icon} />} label={pasteLabel} onSelect={run(onPaste)} />}
      <MenuItem icon={<BoxSelect className={icon} />} label="Select all" onSelect={run(onSelectAll)} />
      <MenuSeparator />
      {onTidy && <MenuItem icon={<LayoutGrid className={icon} />} label="Tidy layout" onSelect={run(onTidy)} />}
      <MenuItem icon={<Maximize className={icon} />} label="Zoom to fit" onSelect={run(onZoomToFit)} />
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
      {hiddenCardCount > 0 && onShowHiddenCards && (
        <>
          <MenuSeparator />
          <MenuItem
            icon={<Eye className={icon} />}
            label={`Show ${hiddenCardCount} hidden ${hiddenCardCount === 1 ? 'card' : 'cards'}`}
            onSelect={run(onShowHiddenCards)}
          />
        </>
      )}
      {onExport && (
        <>
          <MenuSeparator />
          <MenuGroup label="Export this view">
            <MenuItem icon={<FileImage className={icon} />} label="SVG" onSelect={run(() => onExport('svg'))} />
            <MenuItem icon={<Image className={icon} />} label="PNG" onSelect={run(() => onExport('png'))} />
            <MenuItem icon={<FileCode className={icon} />} label="Mermaid" onSelect={run(() => onExport('mermaid'))} />
            <MenuItem icon={<Network className={icon} />} label="Structurizr DSL (whole model)" onSelect={run(() => onExport('dsl'))} />
          </MenuGroup>
        </>
      )}
      {onCopyLink && (
        <>
          <MenuSeparator />
          <MenuItem icon={<Link className={icon} />} label="Copy link to this view" onSelect={run(onCopyLink)} />
        </>
      )}
    </ContextMenu>
  )
}
