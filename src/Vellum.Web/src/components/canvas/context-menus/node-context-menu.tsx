import {
  ArrowRightFromLine,
  BadgeCheck,
  ChevronRight,
  CircleHelp,
  Clipboard,
  Copy,
  EyeOff,
  Fingerprint,
  Maximize2,
  Minimize2,
  Pencil,
  Plus,
  Share2,
  Trash2,
} from 'lucide-react'
import { ContextMenu, MenuItem, MenuSeparator } from './menu'
import { LABEL_ACTION_TEXT } from '@/lib/fact-labels'

interface NodeContextMenuProps {
  position: { x: number; y: number }
  hasChildren: boolean
  isExpanded: boolean
  /** The kinds a person can add inside this element. Empty for an element that holds none, or a model that cannot change. */
  childKinds: string[]
  /** False for a node that is shown inside an expanded parent: it expands on its own level only. */
  canExpand: boolean
  /** True for an element that can hold others: its inside opens as a level of its own. */
  canOpenInside: boolean
  onAddChild: (kind: string) => void
  onClose: () => void
  onEditDetails: () => void
  onExpand: () => void
  onOpenInside: () => void
  onSelectConnected: () => void
  onCopyId: () => void
  /** Copies the card for a paste. Not offered where nothing can be pasted. */
  onCopy?: () => void
  onHide?: () => void
  onAddRelationship?: () => void
  onDuplicate: () => void
  onDelete: () => void
  /** The one label change a person can make to this fact, or null. */
  labelAction?: 'accept' | 'mark-unknown' | null
  onLabelAction?: () => void
  /** The model cannot be changed: the menu shows what looks and leaves out what writes. */
  readOnly?: boolean
}

const icon = 'h-3.5 w-3.5'

export function NodeContextMenu({
  position,
  hasChildren,
  isExpanded,
  childKinds,
  canExpand,
  canOpenInside,
  onAddChild,
  onClose,
  onEditDetails,
  onExpand,
  onOpenInside,
  onSelectConnected,
  onCopyId,
  onCopy,
  onHide,
  onAddRelationship,
  onDuplicate,
  onDelete,
  labelAction = null,
  onLabelAction,
  readOnly = false,
}: NodeContextMenuProps) {
  const run = (action: () => void) => () => { action(); onClose() }

  return (
    <ContextMenu position={position} label="Element" onClose={onClose}>
      <MenuItem icon={<Pencil className={icon} />} label={readOnly ? 'Show details' : 'Edit details'} onSelect={run(onEditDetails)} />
      {canOpenInside && <MenuItem icon={<ChevronRight className={icon} />} label="Open inside" onSelect={run(onOpenInside)} />}
      {hasChildren && canExpand && (
        <MenuItem
          icon={isExpanded ? <Minimize2 className={icon} /> : <Maximize2 className={icon} />}
          label={isExpanded ? 'Collapse' : 'Expand'}
          onSelect={run(onExpand)}
        />
      )}
      {childKinds.length > 0 && (
        <>
          <MenuSeparator />
          {childKinds.map((kind) => (
            <MenuItem key={kind} icon={<Plus className={icon} />} label={`Add ${kind} inside`} onSelect={run(() => onAddChild(kind))} />
          ))}
        </>
      )}
      {onAddRelationship && (
        <MenuItem icon={<ArrowRightFromLine className={icon} />} label="Add a relationship from here" onSelect={run(onAddRelationship)} />
      )}
      <MenuSeparator />
      <MenuItem icon={<Share2 className={icon} />} label="Select connected" onSelect={run(onSelectConnected)} />
      {onHide && <MenuItem icon={<EyeOff className={icon} />} label="Hide from this view" onSelect={run(onHide)} />}
      {onCopy && <MenuItem icon={<Clipboard className={icon} />} label="Copy" onSelect={run(onCopy)} />}
      <MenuItem icon={<Fingerprint className={icon} />} label="Copy ID" onSelect={run(onCopyId)} />
      {!readOnly && (
        <>
          <MenuSeparator />
          {labelAction && onLabelAction && (
            <MenuItem
              icon={labelAction === 'accept' ? <BadgeCheck className={icon} /> : <CircleHelp className={icon} />}
              label={LABEL_ACTION_TEXT[labelAction]}
              onSelect={run(onLabelAction)}
            />
          )}
          <MenuItem icon={<Copy className={icon} />} label="Duplicate" onSelect={run(onDuplicate)} />
          <MenuSeparator />
          <MenuItem icon={<Trash2 className={icon} />} label="Delete" onSelect={run(onDelete)} destructive />
        </>
      )}
    </ContextMenu>
  )
}
