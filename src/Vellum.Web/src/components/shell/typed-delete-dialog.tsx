import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

interface TypedDeleteDialogProps {
  /** "project" or "workspace": the dialog says what is deleted. */
  noun: string
  /** The name the person types to confirm. */
  name: string
  /** What goes with it, one line each. */
  items: ReactNode[]
  /** What stays, shown after the list. */
  note?: ReactNode
  pending?: boolean
  error?: string | null
  onConfirm: () => void
  onClose: () => void
}

/**
 * The delete of a project or a workspace. It cannot be undone and removes data from every
 * module, so the dialog lists what goes and the button stays off until the name is typed.
 */
export function TypedDeleteDialog({ noun, name, items, note, pending, error, onConfirm, onClose }: TypedDeleteDialogProps) {
  const [typed, setTyped] = useState('')
  const matches = typed.trim() === name

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !pending) onClose() }}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete {noun} “{name}”</DialogTitle>
          <DialogDescription>This cannot be undone. These go with the {noun}:</DialogDescription>
        </DialogHeader>
        <ul className="max-h-72 list-disc space-y-1 overflow-y-auto pl-5 text-xs text-muted-foreground">
          {items.map((item, i) => <li key={i}>{item}</li>)}
        </ul>
        {note && <p className="text-xs text-muted-foreground">{note}</p>}
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (matches && !pending) onConfirm()
          }}
        >
          <label className="block text-xs text-muted-foreground">
            Type <span className="font-mono text-foreground">{name}</span> to confirm
            <Input className="mt-1" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" data-autofocus />
          </label>
          {error && <p role="alert" className="mt-2 text-xs text-destructive">{error}</p>}
          <DialogFooter className="mt-4">
            <Button type="button" variant="ghost" disabled={pending} onClick={onClose}>Cancel</Button>
            <Button type="submit" variant="destructive" disabled={!matches || pending}>
              {pending ? 'Deleting…' : `Delete ${noun}`}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
