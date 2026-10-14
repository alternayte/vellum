import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

interface TextPromptDialogProps {
  title: string
  label: string
  initial: string
  submitLabel: string
  /** A description of several lines. An empty value is allowed only here. */
  multiline?: boolean
  pending?: boolean
  error?: string | null
  onSubmit: (value: string) => void
  onClose: () => void
}

/** Asks for one text value: a new name, a description. The dialog stays open until the caller closes it. */
export function TextPromptDialog({ title, label, initial, submitLabel, multiline, pending, error, onSubmit, onClose }: TextPromptDialogProps) {
  const [value, setValue] = useState(initial)
  const trimmed = value.trim()
  const valid = multiline || trimmed.length > 0

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent aria-describedby={undefined}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (valid && !pending) onSubmit(multiline ? value : trimmed)
          }}
        >
          <label className="block text-xs text-muted-foreground">
            {label}
            {multiline ? (
              <Textarea className="mt-1" rows={4} value={value} onChange={(e) => setValue(e.target.value)} data-autofocus />
            ) : (
              <Input className="mt-1" value={value} onChange={(e) => setValue(e.target.value)} data-autofocus />
            )}
          </label>
          {error && <p role="alert" className="mt-2 text-xs text-destructive">{error}</p>}
          <DialogFooter className="mt-4">
            <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={!valid || pending}>{pending ? 'Saving…' : submitLabel}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
