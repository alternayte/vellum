import { useEffect, useLayoutEffect, useRef, type KeyboardEvent, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

const ITEM = '[role^="menuitem"]:not([aria-disabled="true"])'

/**
 * A context menu that follows the WAI-ARIA menu pattern: the menu takes the focus on its first
 * item, the arrow keys, Home and End move between items, a letter moves to the next item that
 * starts with it, and Escape or Tab closes it. The focus goes back to the control that had it.
 */
export function ContextMenu({
  position, label, onClose, children,
}: {
  position: { x: number; y: number }
  label: string
  onClose: () => void
  children: ReactNode
}) {
  const menu = useRef<HTMLDivElement>(null)

  useLayoutEffect(() => {
    const el = menu.current
    const opener = document.activeElement instanceof HTMLElement && document.activeElement !== document.body ? document.activeElement : null
    el?.querySelector<HTMLElement>(ITEM)?.focus()
    return () => {
      // An item that moved the focus elsewhere (a field, a dialog) keeps it there.
      const active = document.activeElement
      const lost = !active || active === document.body || (el?.contains(active) ?? false)
      if (lost && opener?.isConnected) opener.focus()
    }
  }, [])

  // A menu that opens near the right or bottom edge moves inside the window.
  useEffect(() => {
    const el = menu.current
    if (!el) return
    const box = el.getBoundingClientRect()
    if (box.right > window.innerWidth) el.style.left = `${Math.max(4, window.innerWidth - box.width - 4)}px`
    if (box.bottom > window.innerHeight) el.style.top = `${Math.max(4, window.innerHeight - box.height - 4)}px`
  }, [])

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const items = [...(menu.current?.querySelectorAll<HTMLElement>(ITEM) ?? [])]
    if (items.length === 0) return
    const at = items.indexOf(document.activeElement as HTMLElement)
    const move = (index: number) => {
      e.preventDefault()
      items[(index + items.length) % items.length].focus()
    }
    switch (e.key) {
      case 'ArrowDown': return move(at + 1)
      case 'ArrowUp': return move(at < 0 ? items.length - 1 : at - 1)
      case 'Home': return move(0)
      case 'End': return move(items.length - 1)
      case 'Escape':
        // The Escape is used here, so the workbench does not also clear its selection.
        e.preventDefault()
        e.stopPropagation()
        onClose()
        return
      case 'Tab':
        e.preventDefault()
        onClose()
        return
      default:
        if (e.key.length === 1 && /\S/.test(e.key) && !e.ctrlKey && !e.metaKey && !e.altKey) {
          const letter = e.key.toLowerCase()
          const order = [...items.slice(at + 1), ...items.slice(0, at + 1)]
          const next = order.find((item) => item.textContent?.trim().toLowerCase().startsWith(letter))
          if (next) {
            e.preventDefault()
            next.focus()
          }
        }
    }
  }

  return (
    <>
      <div className="fixed inset-0 z-40" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose() }} />
      <div
        ref={menu}
        role="menu"
        aria-label={label}
        aria-orientation="vertical"
        onKeyDown={onKeyDown}
        className="fixed z-50 max-h-[calc(100dvh-8px)] min-w-[180px] max-w-[calc(100vw-8px)] overflow-y-auto rounded-lg border border-border bg-popover p-1 shadow-lg"
        style={{ left: position.x, top: position.y }}
      >
        {children}
      </div>
    </>
  )
}

const itemClass =
  'flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-sm outline-none hover:bg-accent focus-visible:bg-accent focus-visible:ring-2 focus-visible:ring-ring/60'

/** One action of a menu. `checked` makes it one choice of a group (menuitemradio). */
export function MenuItem({
  icon, label, onSelect, destructive, checked,
}: {
  icon: ReactNode
  label: string
  onSelect: () => void
  destructive?: boolean
  checked?: boolean
}) {
  return (
    <button
      type="button"
      role={checked === undefined ? 'menuitem' : 'menuitemradio'}
      aria-checked={checked}
      tabIndex={-1}
      className={cn(itemClass, destructive ? 'text-destructive' : 'text-popover-foreground', checked && 'bg-accent')}
      onClick={onSelect}
    >
      <span aria-hidden="true" className="flex h-3.5 w-3.5 items-center justify-center">{icon}</span>
      {label}
    </button>
  )
}

export function MenuSeparator() {
  return <div role="separator" className="my-1 h-px bg-border" />
}

/** A labelled group of items, such as the choices of one setting. */
export function MenuGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div role="group" aria-label={label}>
      <div aria-hidden="true" className="px-2 py-1 text-[10px] uppercase tracking-wider text-muted-foreground">{label}</div>
      {children}
    </div>
  )
}
