import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { Check, ChevronsUpDown } from 'lucide-react'
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

export interface PickerOption {
  id: string
  label: string
  /** A second line: the owner, the kind or the contract values. */
  detail?: string
  group?: string
}

/**
 * A combobox over parts of the model, chosen by name (WAI-ARIA combobox with a listbox
 * popup). Typing filters by label and detail. The value is the ID; the person never sees it.
 * A value that is not among the options (a part that was removed) is named as missing.
 */
export function ReferencePicker({
  label, value, options, onChange, placeholder = 'Choose…', emptyText = 'Nothing to choose', allowNone, noneLabel = 'None', invalid, className,
}: {
  label: string
  value: string | null
  options: PickerOption[]
  onChange: (id: string | null) => void
  placeholder?: string
  emptyText?: string
  /** Offers an entry that clears the value. */
  allowNone?: boolean
  noneLabel?: string
  invalid?: boolean
  className?: string
}) {
  const id = useId()
  const listId = `${id}-list`
  const input = useRef<HTMLInputElement>(null)
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)

  const selected = value ? options.find((o) => o.id === value) : null
  const shown = useMemo(() => {
    const q = query.trim().toLowerCase()
    const matches = q ? options.filter((o) => `${o.label} ${o.detail ?? ''} ${o.group ?? ''}`.toLowerCase().includes(q)) : options
    return allowNone && !q ? [{ id: '', label: noneLabel } as PickerOption, ...matches] : matches
  }, [options, query, allowNone, noneLabel])

  const show = () => {
    setQuery('')
    setActive(Math.max(0, shown.findIndex((o) => o.id === (value ?? ''))))
    setOpen(true)
  }
  const choose = (option: PickerOption) => {
    onChange(option.id || null)
    setOpen(false)
    setQuery('')
    input.current?.focus()
  }

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      if (!open) return show()
      if (shown.length === 0) return
      setActive((i) => (i + (e.key === 'ArrowDown' ? 1 : -1) + shown.length) % shown.length)
    } else if (e.key === 'Enter' && open) {
      e.preventDefault()
      if (shown[active]) choose(shown[active])
    } else if (e.key === 'Home' && open) {
      e.preventDefault()
      setActive(0)
    } else if (e.key === 'End' && open) {
      e.preventDefault()
      setActive(Math.max(0, shown.length - 1))
    } else if (e.key === 'Tab') {
      setOpen(false)
    }
  }

  const display = open ? query : selected ? selected.label : value ? 'Not in the model' : ''
  const activeId = open && shown[active] ? `${id}-option-${active}` : undefined

  useEffect(() => {
    if (activeId) document.getElementById(activeId)?.scrollIntoView({ block: 'nearest' })
  }, [activeId])

  return (
    <div className={cn('min-w-0', className)}>
      <label htmlFor={id} className="block text-[11px] font-medium text-muted-foreground">{label}</label>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverAnchor asChild>
          <div className="relative mt-1">
            <input
              ref={input}
              id={id}
              role="combobox"
              aria-expanded={open}
              aria-controls={listId}
              aria-autocomplete="list"
              aria-activedescendant={activeId}
              aria-invalid={invalid || (value && !selected) ? true : undefined}
              autoComplete="off"
              spellCheck={false}
              value={display}
              placeholder={placeholder}
              onChange={(e) => { setQuery(e.target.value); setActive(0); if (!open) setOpen(true) }}
              onClick={() => (open ? setOpen(false) : show())}
              onKeyDown={onKeyDown}
              title={selected?.detail}
              className={cn(
                'h-8 w-full rounded-lg border border-input bg-transparent pr-7 pl-2 text-sm text-foreground outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive',
                value && !selected && !open && 'text-[var(--trace-failure)]',
              )}
            />
            <ChevronsUpDown aria-hidden="true" className="pointer-events-none absolute top-1/2 right-2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          </div>
        </PopoverAnchor>
        <PopoverContent
          align="start"
          className="w-(--radix-popover-trigger-width) min-w-64 gap-0 p-1"
          onOpenAutoFocus={(e) => e.preventDefault()}
          onCloseAutoFocus={(e) => e.preventDefault()}
          onInteractOutside={(e) => { if (e.target === input.current) e.preventDefault() }}
        >
          <ul id={listId} role="listbox" aria-label={label} className="max-h-64 overflow-y-auto">
            {shown.length === 0 && <li className="px-2 py-1.5 text-xs text-muted-foreground">{query ? `Nothing matches “${query}”` : emptyText}</li>}
            {shown.map((option, index) => {
              const groupStart = option.group && option.group !== shown[index - 1]?.group
              return (
                <li key={option.id || 'none'} role="presentation">
                  {groupStart && <p aria-hidden="true" className="px-2 pt-1.5 pb-0.5 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">{option.group}</p>}
                  <div
                    id={`${id}-option-${index}`}
                    role="option"
                    aria-selected={option.id === (value ?? '')}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => choose(option)}
                    onMouseMove={() => setActive(index)}
                    className={cn(
                      'flex cursor-default items-start gap-2 rounded px-2 py-1.5 text-left text-sm text-popover-foreground',
                      index === active && 'bg-accent',
                    )}
                  >
                    <Check aria-hidden="true" className={cn('mt-0.5 h-3.5 w-3.5 shrink-0', option.id === (value ?? '') ? 'opacity-100' : 'opacity-0')} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate">{option.label}</span>
                      {option.detail && <span className="block truncate font-mono text-[10px] text-muted-foreground">{option.detail}</span>}
                    </span>
                  </div>
                </li>
              )
            })}
          </ul>
        </PopoverContent>
      </Popover>
    </div>
  )
}
