import { useId, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { branchesApi } from '@/lib/branches-api'
import { describeError } from '@/lib/costs-api'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { useDraftStore, type ModelSelection } from '@/stores/draft-store'

type RevisionSelection = Extract<ModelSelection, { kind: 'revision' }>
type VariantSelection = Extract<ModelSelection, { kind: 'variant' }>

/** A new name for the revision that the workbench shows. Its stored model does not change. */
export function RenameRevisionDialog({ projectId, revision, onClose }: { projectId: string; revision: RevisionSelection; onClose: () => void }) {
  const queryClient = useQueryClient()
  const select = useDraftStore((s) => s.select)
  const [name, setName] = useState(revision.name)
  const nameId = useId()
  const rename = useMutation({
    mutationFn: () => branchesApi.renameRevision(projectId, revision.id, name.trim()),
    onSuccess: async (renamed) => {
      select({ kind: 'revision', id: renamed.id, name: renamed.name })
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'revisions'] })
      onClose()
    },
    onError: shownInline,
  })

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent>
        <form onSubmit={(e) => { e.preventDefault(); rename.mutate() }} className="space-y-4">
          <DialogHeader>
            <DialogTitle>Rename the revision {revision.name}</DialogTitle>
            <DialogDescription>The model of the revision does not change. A check, a snapshot or an export that names the revision keeps its record.</DialogDescription>
          </DialogHeader>
          <div>
            <label htmlFor={nameId} className="mb-1 block text-xs text-muted-foreground">Name</label>
            <Input id={nameId} value={name} onChange={(e) => setName(e.target.value)} required autoFocus maxLength={200} />
          </div>
          {rename.isError && <p role="alert" className="text-xs text-destructive">The revision was not renamed. {describeError(rename.error)}</p>}
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={rename.isPending || name.trim() === '' || name.trim() === revision.name}>
              {rename.isPending ? 'Renaming…' : 'Rename'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/**
 * Deletes the revision that the workbench shows. The server refuses while a promise run, a
 * review snapshot or a variant names the revision, and the dialog lists each of them.
 */
export function DeleteRevisionDialog({ projectId, revision, onClose }: { projectId: string; revision: RevisionSelection; onClose: () => void }) {
  const queryClient = useQueryClient()
  const select = useDraftStore((s) => s.select)
  const remove = useMutation({
    mutationFn: () => branchesApi.deleteRevision(projectId, revision.id),
    onSuccess: async () => {
      select({ kind: 'main' })
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'revisions'] })
      onClose()
    },
    onError: shownInline,
  })
  const references = remove.error instanceof ApiError && remove.error.status === 409 ? remove.error.fields.map((f) => f.message) : []

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Delete the revision {revision.name}?</DialogTitle>
          <DialogDescription>
            Main does not change. A revision that a promise run, a review snapshot or a variant names stays, so that record stays readable and repeatable.
          </DialogDescription>
        </DialogHeader>
        {remove.isError && references.length === 0 && (
          <p role="alert" className="text-xs text-destructive">The revision was not deleted. {describeError(remove.error)}</p>
        )}
        {references.length > 0 && (
          <div role="alert" className="space-y-1.5 rounded-md border border-border p-3 text-xs">
            <p className="text-foreground">The revision stays. These records name it:</p>
            <ul className="list-disc space-y-0.5 pl-4 text-muted-foreground">
              {references.map((reference) => <li key={reference}>{reference}</li>)}
            </ul>
          </div>
        )}
        <DialogFooter>
          <Button type="button" variant="ghost" data-autofocus onClick={onClose}>Keep the revision</Button>
          <Button type="button" variant="destructive" disabled={remove.isPending || references.length > 0} onClick={() => remove.mutate()}>
            {remove.isPending ? 'Deleting…' : 'Delete revision'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** Deletes the variant that the workbench shows, with its cost sheet and its saved estimates. */
export function DeleteVariantDialog({ projectId, variant, onClose }: { projectId: string; variant: VariantSelection; onClose: () => void }) {
  const queryClient = useQueryClient()
  const select = useDraftStore((s) => s.select)
  const remove = useMutation({
    mutationFn: () => branchesApi.deleteVariant(projectId, variant.id),
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
          <DialogTitle>Delete the variant {variant.name}?</DialogTitle>
          <DialogDescription>
            Vellum removes the variant, its cost sheet and its saved estimates. Main does not change. A stress run or a check that ran on the variant keeps its record.
          </DialogDescription>
        </DialogHeader>
        {remove.isError && <p role="alert" className="text-xs text-destructive">The variant was not deleted. {describeError(remove.error)}</p>}
        <DialogFooter>
          <Button type="button" variant="ghost" data-autofocus onClick={onClose}>Keep the variant</Button>
          <Button type="button" variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate()}>
            {remove.isPending ? 'Deleting…' : 'Delete variant'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
