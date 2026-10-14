import type { ReactNode } from 'react'
import { DropdownMenu } from 'radix-ui'
import { MoreHorizontal } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface ActionMenuItem {
  label: string
  icon?: ReactNode
  onSelect: () => void
  destructive?: boolean
  disabled?: boolean
}

interface ActionMenuProps {
  /** The accessible name of the "⋯" button, for example "Actions for Orders". */
  label: string
  items: ActionMenuItem[]
  /** Classes of the trigger button. */
  className?: string
}

/** A "⋯" button that opens a list of actions. A destructive action is last and red. */
export function ActionMenu({ label, items, className }: ActionMenuProps) {
  if (items.length === 0) return null
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <button
          type="button"
          aria-label={label}
          title="More actions"
          onClick={(e) => e.stopPropagation()}
          className={cn(
            'flex h-6 w-6 shrink-0 items-center justify-center rounded text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary data-[state=open]:bg-muted data-[state=open]:text-foreground',
            className,
          )}
        >
          <MoreHorizontal aria-hidden="true" className="h-4 w-4" />
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={4}
          onClick={(e) => e.stopPropagation()}
          className="z-50 min-w-44 rounded-lg bg-popover p-1 text-sm text-popover-foreground shadow-md ring-1 ring-foreground/10"
        >
          {items.map((item) => (
            <DropdownMenu.Item
              key={item.label}
              disabled={item.disabled}
              onSelect={item.onSelect}
              className={cn(
                'flex cursor-default items-center gap-2 rounded-md px-2 py-1.5 text-xs outline-none data-[disabled]:opacity-50',
                item.destructive
                  ? 'text-destructive data-[highlighted]:bg-destructive/10'
                  : 'data-[highlighted]:bg-muted',
              )}
            >
              {item.icon}
              {item.label}
            </DropdownMenu.Item>
          ))}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}
