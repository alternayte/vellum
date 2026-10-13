import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { canContain, validKindsForContext } from '@/lib/containment-rules'

interface ElementChoice {
  id: string
  kind: string
  name: string
}

interface AddElementDialogProps {
  /** The element the dialog starts in; null is the top level. */
  parentId: string | null
  /** The kind the dialog starts with, when the parent can hold it. */
  kind?: string
  elements: ElementChoice[]
  pending: boolean
  onClose: () => void
  onAdd: (name: string, kind: string, parentId: string | null) => void
}

const KIND_LABELS: Record<string, string> = {
  actor: 'Actor',
  system: 'System',
  app: 'App',
  store: 'Store',
  component: 'Component',
}

const selectClass =
  'h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50'

/**
 * Adds one element at the top level or inside a system or an app. The kinds on offer are
 * the kinds the chosen parent can hold; the server holds the same rule.
 */
export function AddElementDialog({ parentId: startParentId, kind: startKind, elements, pending, onClose, onAdd }: AddElementDialogProps) {
  const parents = elements.filter((element) => canContain(element.kind))
  const [parentId, setParentId] = useState(parents.some((p) => p.id === startParentId) ? startParentId : null)
  const [kind, setKind] = useState(startKind)
  const [name, setName] = useState('')

  const kinds = validKindsForContext(parentId ? parents.find((p) => p.id === parentId)?.kind ?? null : null)
  const chosenKind = kind && kinds.includes(kind) ? kind : kinds.includes('system') ? 'system' : kinds[0]
  const parent = parents.find((p) => p.id === parentId)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add Element</DialogTitle>
          <DialogDescription>
            {parent
              ? `The element goes inside ${parent.name}. A ${parent.kind} holds ${kinds.map((k) => `${k}s`).join(' and ')}.`
              : 'The element goes on the top level. To add an app or a store, choose the system it goes inside.'}
          </DialogDescription>
        </DialogHeader>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (name.trim()) onAdd(name.trim(), chosenKind, parentId)
          }}
        >
          <div className="space-y-3">
            <label className="block text-xs font-medium text-muted-foreground">
              Inside
              <select value={parentId ?? ''} onChange={(e) => setParentId(e.target.value || null)} className={`mt-1 ${selectClass}`}>
                <option value="">Top level</option>
                {parents.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.kind})</option>)}
              </select>
            </label>
            <label className="block text-xs font-medium text-muted-foreground">
              Kind
              <select value={chosenKind} onChange={(e) => setKind(e.target.value)} className={`mt-1 ${selectClass}`}>
                {kinds.map((k) => <option key={k} value={k}>{KIND_LABELS[k] ?? k}</option>)}
              </select>
            </label>
            <label className="block text-xs font-medium text-muted-foreground">
              Name
              <Input className="mt-1" placeholder="Element name" value={name} onChange={(e) => setName(e.target.value)} data-autofocus />
            </label>
          </div>
          <DialogFooter className="mt-4">
            <Button type="submit" disabled={!name.trim() || pending}>{pending ? 'Adding...' : 'Add'}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
