import { useState } from 'react'
import { Check } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'

export interface PickOption {
  /** Null is the "none" choice, for example "No space". */
  value: string | null
  label: string
  hint?: string
}

interface PickDialogProps {
  title: string
  options: PickOption[]
  current: string | null
  submitLabel: string
  pending?: boolean
  error?: string | null
  onSubmit: (value: string | null) => void
  onClose: () => void
}

/** Picks one of a list, with a filter for a long list. */
export function PickDialog({ title, options, current, submitLabel, pending, error, onSubmit, onClose }: PickDialogProps) {
  const [picked, setPicked] = useState<string | null>(current)
  const [filter, setFilter] = useState('')
  const q = filter.trim().toLowerCase()
  const shown = q ? options.filter((o) => o.label.toLowerCase().includes(q)) : options

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !pending) onClose() }}>
      <DialogContent aria-describedby={undefined}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        {options.length > 8 && (
          <Input aria-label="Filter" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} data-autofocus />
        )}
        <ul role="listbox" aria-label={title} className="max-h-64 overflow-y-auto rounded-md border border-border">
          {shown.map((option) => (
            <li key={option.value ?? 'none'} role="option" aria-selected={picked === option.value}>
              <button
                type="button"
                className={cn(
                  'flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-sm hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary',
                  picked === option.value && 'bg-accent text-accent-foreground',
                )}
                onClick={() => setPicked(option.value)}
                onDoubleClick={() => { if (!pending) onSubmit(option.value) }}
              >
                <Check aria-hidden="true" className={cn('h-3.5 w-3.5 shrink-0', picked === option.value ? 'opacity-100' : 'opacity-0')} />
                <span className={cn('min-w-0 flex-1 truncate', option.value === null && 'text-muted-foreground')}>{option.label}</span>
                {option.hint && <span className="font-mono text-[10px] uppercase text-muted-foreground">{option.hint}</span>}
              </button>
            </li>
          ))}
          {shown.length === 0 && <li className="px-2.5 py-2 text-xs text-muted-foreground">No matches</li>}
        </ul>
        {error && <p role="alert" className="text-xs text-destructive">{error}</p>}
        <DialogFooter>
          <Button type="button" variant="ghost" disabled={pending} onClick={onClose}>Cancel</Button>
          <Button type="button" disabled={pending || picked === current} onClick={() => onSubmit(picked)}>
            {pending ? 'Saving…' : submitLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
