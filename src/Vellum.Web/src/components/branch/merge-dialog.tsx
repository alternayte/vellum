import { useEffect, useId } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { useChannels } from '@/hooks/use-contracts'
import { useElements } from '@/hooks/use-elements'
import { branchesApi } from '@/lib/branches-api'
import { describeError } from '@/lib/costs-api'
import { ENTITY_LABEL, ENTITY_ORDER, changedFields, entityLabel } from '@/lib/model-diff'
import { shownInline } from '@/lib/mutation-errors'
import { useDraftStore, type MergeConflict, type ModelSelection } from '@/stores/draft-store'
import { ChangeChip, FieldTable } from './change-list'

interface Props {
  projectId: string
  draft: Extract<ModelSelection, { kind: 'draft' }>
  /** False for a viewer: the diff is shown and the merge is not offered. */
  canMerge: boolean
  onClose: () => void
}

const CONFLICT_REASON: Record<string, string> = {
  both_modified: 'Main and the draft changed this in different ways.',
  delete_modify: 'One side deleted this and the other side changed it.',
  orphaned_child: 'One side deleted an element and the other side put a child in it.',
  referenced_by_contract: 'One side deleted this and a contract of the other side names it.',
  dangling_operation: 'The owner of this operation does not exist after the merge.',
  dangling_subscription: 'This subscription names something that does not exist after the merge.',
  dangling_binding: 'This binding names something that does not exist after the merge.',
}

type Value = Record<string, unknown> | null

/**
 * What a merge of the draft does to main, change by change, with a choice for each
 * conflict, and the merge itself. The list is the merge preview of the server. A conflict
 * between the two cost sheets has no choice here: the server refuses the merge until the
 * sheets agree.
 */
export function MergeDialog({ projectId, draft, canMerge, onClose }: Props) {
  const queryClient = useQueryClient()
  const id = useId()
  const { resolutions, setResolution, select, enterReviewMode, leaveReviewMode } = useDraftStore()

  // A new look at the draft starts with no resolution from an earlier look.
  useEffect(() => { leaveReviewMode() }, [leaveReviewMode])

  const preview = useQuery({
    queryKey: ['projects', projectId, 'drafts', draft.id, 'merge-preview'],
    queryFn: () => branchesApi.mergePreview(projectId, draft.id),
    staleTime: 0, gcTime: 0,
  })
  // The values of main and of the draft, in the shapes the lists give, for the fields of a change.
  const values = useQuery({
    queryKey: ['projects', projectId, 'model-compare', 'main', draft.streamId],
    queryFn: () => branchesApi.compareModels(projectId, { kind: 'main' }, draft),
    staleTime: 0, gcTime: 0,
  })
  const mainElements = useElements(projectId)
  const draftElements = useElements(projectId, draft.streamId)
  const mainChannels = useChannels(projectId)
  const draftChannels = useChannels(projectId, draft.streamId)
  const names = (entityId: string) =>
    [...(draftElements.data ?? []), ...(mainElements.data ?? []), ...(draftChannels.data ?? []), ...(mainChannels.data ?? [])].find((e) => e.id === entityId)?.name
  const valuesOf = (entityId: string) => values.data?.changes.find((c) => c.entityId === entityId)

  const merge = useMutation({
    mutationFn: () => branchesApi.merge(
      projectId, draft.id,
      Object.entries(resolutions).map(([entityId, resolution]) => ({ entityId, resolution })),
      preview.data!.mainVersion),
    onSuccess: async () => {
      select({ kind: 'main' })
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
      toast.success(`The draft ${draft.name} is merged into main.`)
      onClose()
    },
    onError: shownInline,
  })

  const data = preview.data
  const conflicts = data?.conflicts ?? []
  const changes = data?.autoResolved ?? []
  const unresolved = conflicts.filter((c) => resolutions[c.entityId] === undefined).length
  const sheetConflicts = data?.costSheet.conflicts ?? []
  const count = (kind: string) => changes.filter((c) => c.changeKind === kind).length
  const nothing = data !== undefined && changes.length === 0 && conflicts.length === 0 && data.costSheet.changes === 0

  const conflictValues = (conflict: MergeConflict) => ({ main: conflict.oursValue as Value, draft: conflict.theirsValue as Value })

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Draft {draft.name} against main</DialogTitle>
          <DialogDescription>
            These are the changes that a merge of the draft makes to main.
            {data ? ` The preview read main at version ${data.mainVersion}.` : ''} A merge writes the model and the cost sheet together, or nothing.
          </DialogDescription>
        </DialogHeader>

        {preview.isLoading && <p className="text-xs text-muted-foreground">Reading the draft and main…</p>}
        {preview.isError && <p role="alert" className="text-xs text-destructive">{describeError(preview.error)}</p>}

        {data && (
          <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
            <p className="text-xs text-muted-foreground" data-testid="merge-summary">
              {count('added')} added · {count('modified')} modified · {count('removed')} removed
              {conflicts.length > 0 && <span className="text-[var(--trace-retry)]"> · {conflicts.length} {conflicts.length === 1 ? 'conflict' : 'conflicts'}</span>}
              {' '}· cost sheet: {data.costSheet.changes} {data.costSheet.changes === 1 ? 'change' : 'changes'}
            </p>
            {nothing && <p className="text-sm text-foreground">The draft and main hold the same model and the same cost sheet. A merge changes nothing and closes the draft.</p>}

            {conflicts.length > 0 && (
              <section aria-labelledby={`${id}-conflicts`}>
                <h3 id={`${id}-conflicts`} className="text-xs font-medium uppercase tracking-wider text-[var(--trace-retry)]">
                  Conflicts <span className="font-mono">{conflicts.length}</span>
                </h3>
                <p className="mt-1 text-xs text-muted-foreground">Choose one side for each conflict. {unresolved > 0 ? `${unresolved} still ${unresolved === 1 ? 'needs' : 'need'} a choice.` : 'Each conflict has a choice.'}</p>
                <ul className="mt-2 space-y-2">
                  {conflicts.map((conflict) => {
                    const sides = conflictValues(conflict)
                    const label = entityLabel(conflict.entityType, sides.draft ?? sides.main, names, conflict.entityId)
                    const group = `${id}-${conflict.entityId}`
                    return (
                      <li key={conflict.entityId} className="rounded-md border border-[var(--trace-retry)]/50 p-2.5">
                        <p className="flex flex-wrap items-center gap-1.5 text-sm text-foreground">
                          <span className="text-[11px] uppercase tracking-wider text-muted-foreground">{conflict.entityType}</span>
                          <span className="min-w-0 break-words font-medium">{label}</span>
                        </p>
                        <p id={`${group}-why`} className="mt-1 text-xs text-muted-foreground">{CONFLICT_REASON[conflict.conflictKind] ?? conflict.conflictKind}</p>
                        <FieldTable left="main" right="draft" fields={changedFields(sides.main, sides.draft)} empty={sides.main === null ? 'Main does not have it.' : sides.draft === null ? 'The draft does not have it.' : 'The two sides hold the same fields.'} />
                        {canMerge && (
                          <fieldset className="mt-2 flex flex-wrap gap-x-4 gap-y-1" aria-describedby={`${group}-why`}>
                            <legend className="sr-only">Resolution for {label}</legend>
                            {([['keep_ours', sides.main === null ? 'Keep main: it stays deleted' : 'Keep main'], ['take_theirs', sides.draft === null ? 'Take the draft: delete it' : 'Take the draft']] as const).map(([value, text]) => (
                              <label key={value} className="flex items-center gap-1.5 text-xs text-foreground">
                                <input type="radio" name={group} value={value} checked={resolutions[conflict.entityId] === value} onChange={() => setResolution(conflict.entityId, value)} className="accent-[var(--primary)]" />
                                {text}
                              </label>
                            ))}
                          </fieldset>
                        )}
                      </li>
                    )
                  })}
                </ul>
              </section>
            )}

            {ENTITY_ORDER.filter((type) => changes.some((c) => c.entityType === type)).map((type) => {
              const list = changes.filter((c) => c.entityType === type)
              return (
                <section key={type} aria-label={ENTITY_LABEL[type]}>
                  <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">{ENTITY_LABEL[type]} <span className="font-mono">{list.length}</span></h3>
                  <ul className="mt-1.5 space-y-1">
                    {list.map((change) => {
                      const sides = valuesOf(change.entityId)
                      const value = (sides?.right ?? sides?.left ?? change.resolvedValue) as Value
                      const fields = sides ? changedFields(sides.left, sides.right) : []
                      return (
                        <li key={change.entityId} className="rounded-md border border-border px-2.5 py-1.5">
                          <details>
                            <summary className="flex cursor-pointer items-center gap-2 text-sm text-foreground focus-visible:outline-2 focus-visible:outline-primary">
                              <ChangeChip kind={change.changeKind} />
                              <span className="min-w-0 flex-1 break-words">{entityLabel(type, value, names, change.entityId)}</span>
                            </summary>
                            <FieldTable left="main" right="draft" fields={fields} empty={values.isLoading ? 'Reading the fields…' : 'No field to show.'} />
                          </details>
                        </li>
                      )
                    })}
                  </ul>
                </section>
              )
            })}

            <section aria-labelledby={`${id}-sheet`}>
              <h3 id={`${id}-sheet`} className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Cost sheet</h3>
              <p className="mt-1 text-xs text-foreground">
                {data.costSheet.changes === 0
                  ? 'The merge does not change the cost sheet of main.'
                  : `The merge changes ${data.costSheet.changes} ${data.costSheet.changes === 1 ? 'resource or assumption' : 'resources and assumptions'} in the cost sheet of main.`}
              </p>
              {sheetConflicts.length > 0 && (
                <div role="alert" className="mt-1.5 rounded-md border border-[var(--trace-retry)]/50 p-2.5 text-xs">
                  <p className="font-medium text-[var(--trace-retry)]">The two cost sheets do not merge.</p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-foreground">{sheetConflicts.map((reason) => <li key={reason}>{reason}</li>)}</ul>
                  <p className="mt-1 text-muted-foreground">A cost sheet conflict has no side to choose. Open Costs, change the sheet of the draft or of main until the two agree, then open this diff again.</p>
                </div>
              )}
            </section>
          </div>
        )}

        {merge.isError && <p role="alert" className="text-xs text-destructive">Nothing was merged. {describeError(merge.error)}</p>}
        <DialogFooter className="flex-wrap">
          {data && !nothing && (
            <Button type="button" variant="ghost" onClick={() => { enterReviewMode(data); onClose() }}>Show on the canvas</Button>
          )}
          <Button type="button" variant="ghost" onClick={onClose}>Close</Button>
          {canMerge && (
            <Button type="button" disabled={!data || unresolved > 0 || sheetConflicts.length > 0 || merge.isPending} onClick={() => merge.mutate()}>
              {merge.isPending ? 'Merging…' : 'Merge into main'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
