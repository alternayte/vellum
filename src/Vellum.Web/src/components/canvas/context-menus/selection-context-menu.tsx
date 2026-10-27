import {
  AlignCenterHorizontal,
  AlignCenterVertical,
  AlignEndHorizontal,
  AlignEndVertical,
  AlignHorizontalDistributeCenter,
  AlignStartHorizontal,
  AlignStartVertical,
  AlignVerticalDistributeCenter,
  EyeOff,
  LayoutGrid,
  Trash2,
} from 'lucide-react'
import type { AlignEdge, DistributeAxis } from '@/lib/arrange'
import { ContextMenu, MenuGroup, MenuItem, MenuSeparator } from './menu'

interface SelectionContextMenuProps {
  position: { x: number; y: number }
  count: number
  onClose: () => void
  onAlign: (edge: AlignEdge) => void
  onDistribute: (axis: DistributeAxis) => void
  onTidy: () => void
  onHide?: () => void
  onDelete: () => void
}

const icon = 'h-3.5 w-3.5'

/** The menu of two or more selected cards: arrange them, hide them from the view, or delete them. */
export function SelectionContextMenu({ position, count, onClose, onAlign, onDistribute, onTidy, onHide, onDelete }: SelectionContextMenuProps) {
  const run = (action: () => void) => () => { action(); onClose() }

  return (
    <ContextMenu position={position} label={`${count} selected elements`} onClose={onClose}>
      <MenuGroup label="Align">
        <MenuItem icon={<AlignStartVertical className={icon} />} label="Left" onSelect={run(() => onAlign('left'))} />
        <MenuItem icon={<AlignCenterVertical className={icon} />} label="Centre" onSelect={run(() => onAlign('center'))} />
        <MenuItem icon={<AlignEndVertical className={icon} />} label="Right" onSelect={run(() => onAlign('right'))} />
        <MenuItem icon={<AlignStartHorizontal className={icon} />} label="Top" onSelect={run(() => onAlign('top'))} />
        <MenuItem icon={<AlignCenterHorizontal className={icon} />} label="Middle" onSelect={run(() => onAlign('middle'))} />
        <MenuItem icon={<AlignEndHorizontal className={icon} />} label="Bottom" onSelect={run(() => onAlign('bottom'))} />
      </MenuGroup>
      {count >= 3 && (
        <>
          <MenuSeparator />
          <MenuGroup label="Distribute">
            <MenuItem icon={<AlignHorizontalDistributeCenter className={icon} />} label="Space evenly across" onSelect={run(() => onDistribute('horizontal'))} />
            <MenuItem icon={<AlignVerticalDistributeCenter className={icon} />} label="Space evenly down" onSelect={run(() => onDistribute('vertical'))} />
          </MenuGroup>
        </>
      )}
      <MenuSeparator />
      <MenuItem icon={<LayoutGrid className={icon} />} label="Tidy the selection" onSelect={run(onTidy)} />
      {onHide && <MenuItem icon={<EyeOff className={icon} />} label="Hide from this view" onSelect={run(onHide)} />}
      <MenuSeparator />
      <MenuItem icon={<Trash2 className={icon} />} label={`Delete ${count} elements…`} destructive onSelect={run(onDelete)} />
    </ContextMenu>
  )
}
