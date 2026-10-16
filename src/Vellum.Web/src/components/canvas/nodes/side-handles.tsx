import { Handle, Position, useStore } from '@xyflow/react'
import { cn } from '@/lib/utils'
import { CARD_SIDES, type CardSide } from '@/lib/edge-utils'

const POSITIONS: Record<CardSide, Position> = {
  top: Position.Top,
  right: Position.Right,
  bottom: Position.Bottom,
  left: Position.Left,
}

/**
 * One handle on each side of a card, with the side as its ID. The canvas connects in loose
 * mode, so each handle starts a relationship and takes the end of one. Hidden until the card is
 * hovered, and shown on every card while a relationship or one of its ends is dragged.
 */
export function SideHandles({ className, hoverClassName }: { className: string; hoverClassName?: string }) {
  const connecting = useStore((s) => s.connection.inProgress)
  return (
    <>
      {CARD_SIDES.map((side) => (
        <Handle
          key={side}
          id={side}
          type="source"
          position={POSITIONS[side]}
          className={cn(className, 'bg-primary! transition-opacity', connecting ? 'opacity-100' : cn('opacity-0', hoverClassName))}
        />
      ))}
    </>
  )
}
