import { useId, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { selectClass } from '@/components/settings/fields'
import { branchesApi, type Revision } from '@/lib/branches-api'
import { describeError } from '@/lib/costs-api'
import { shownInline } from '@/lib/mutation-errors'
import { useDraftStore, type ModelSelection } from '@/stores/draft-store'

export type BranchKind = 'draft' | 'revision' | 'variant'

interface Props {
  projectId: string
  kind: BranchKind
  revisions: Revision[]
  /** The model the workbench shows. A variant starts from it when it is a revision. */
  selection: ModelSelection
  onClose: () => void
}

const COPY: Record<BranchKind, { title: string; description: string; label: string; submit: string; failed: string }> = {
  draft: {
    title: 'New draft from main',
    description: 'A draft is a copy of main that you change and then merge into main. Main can change while the draft is open. The workbench shows the draft after you create it.',
    label: 'Draft name',
    submit: 'Create draft',
    failed: 'No draft was created.',
  },
  revision: {
    title: 'Name a revision of main',
    description: 'A revision is a copy of main as it is now. It never changes. You can open it read-only, compare it and start a variant from it.',
    label: 'Revision name',
    submit: 'Save revision',
    failed: 'No revision was saved.',
  },
  variant: {
    title: 'New variant',
    description: 'A variant is a second model that you keep next to main. It has its own cost sheet. Vellum cannot merge a variant into main. The workbench shows the variant after you create it.',
    label: 'Variant name',
    submit: 'Create variant',
    failed: 'No variant was created.',
  },
}

/** Creates a draft, a named revision or a variant. The workbench then shows the new draft or variant. */
export function CreateBranchDialog({ projectId, kind, revisions, selection, onClose }: Props) {
  const queryClient = useQueryClient()
  const select = useDraftStore((s) => s.select)
  const [name, setName] = useState('')
  const [note, setNote] = useState('')
  const [base, setBase] = useState(selection.kind === 'revision' ? selection.id : '')
  const id = useId()
  const copy = COPY[kind]

  const create = useMutation({
    mutationFn: async (): Promise<ModelSelection | null> => {
      const trimmed = name.trim()
      if (kind === 'draft') {
        const draft = await branchesApi.createDraft(projectId, trimmed)
        return { kind: 'draft', id: draft.id, streamId: draft.streamId, name: draft.name }
      }
      if (kind === 'variant') {
        const variant = await branchesApi.createVariant(projectId, trimmed, base || null)
        return { kind: 'variant', id: variant.id, streamId: variant.streamId, name: variant.name }
      }
      await branchesApi.createRevision(projectId, trimmed, note.trim() || null)
      return null
    },
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
      if (created) select(created)
      onClose()
    },
    onError: shownInline,
  })

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{copy.title}</DialogTitle>
          <DialogDescription>{copy.description}</DialogDescription>
        </DialogHeader>
        <form className="flex flex-col gap-3" onSubmit={(e) => { e.preventDefault(); if (name.trim()) create.mutate() }}>
          <div>
            <label htmlFor={`${id}-name`} className="mb-1 block text-xs font-medium text-muted-foreground">{copy.label}</label>
            <Input
              id={`${id}-name`} value={name} maxLength={200} required data-autofocus
              aria-invalid={create.isError ? true : undefined}
              aria-describedby={create.isError ? `${id}-error` : undefined}
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          {kind === 'revision' && (
            <div>
              <label htmlFor={`${id}-note`} className="mb-1 block text-xs font-medium text-muted-foreground">Note (optional)</label>
              <Textarea id={`${id}-note`} value={note} rows={2} maxLength={2000} onChange={(e) => setNote(e.target.value)} />
            </div>
          )}
          {kind === 'variant' && (
            <div>
              <label htmlFor={`${id}-base`} className="mb-1 block text-xs font-medium text-muted-foreground">Start from</label>
              <select id={`${id}-base`} className={selectClass} value={base} aria-describedby={`${id}-base-hint`} onChange={(e) => setBase(e.target.value)}>
                <option value="">main, as it is now</option>
                {revisions.map((r) => <option key={r.id} value={r.id}>Revision: {r.name}</option>)}
              </select>
              <p id={`${id}-base-hint`} className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
                The variant copies this model. Its cost sheet starts as a copy of the cost sheet of main.
              </p>
            </div>
          )}
          {create.isError && <p id={`${id}-error`} role="alert" className="text-xs text-destructive">{copy.failed} {describeError(create.error)}</p>}
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={!name.trim() || create.isPending}>{create.isPending ? 'Saving…' : copy.submit}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Asks before a draft is abandoned: the step cannot be reversed. */
export function AbandonDraftDialog({ projectId, draft, onClose }: { projectId: string; draft: Extract<ModelSelection, { kind: 'draft' }>; onClose: () => void }) {
  const queryClient = useQueryClient()
  const select = useDraftStore((s) => s.select)
  const abandon = useMutation({
    mutationFn: () => branchesApi.abandonDraft(projectId, draft.id),
    onSuccess: async () => {
      select({ kind: 'main' })
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
      onClose()
    },
    onError: shownInline,
  })

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Abandon the draft {draft.name}?</DialogTitle>
          <DialogDescription>
            Vellum discards the changes of the draft and its cost sheet. Main does not change. An abandoned draft cannot be opened again.
          </DialogDescription>
        </DialogHeader>
        {abandon.isError && <p role="alert" className="text-xs text-destructive">The draft was not abandoned. {describeError(abandon.error)}</p>}
        <DialogFooter>
          <Button type="button" variant="ghost" data-autofocus onClick={onClose}>Keep the draft</Button>
          <Button type="button" variant="destructive" disabled={abandon.isPending} onClick={() => abandon.mutate()}>
            {abandon.isPending ? 'Abandoning…' : 'Abandon draft'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
