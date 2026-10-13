import { useId, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { importModel, type ImportItem, type ImportResult } from '@/lib/branches-api'
import { describeError } from '@/lib/costs-api'
import { ENTITY_LABEL, ENTITY_ORDER } from '@/lib/model-diff'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { ChangeChip } from './change-list'

interface Props {
  projectId: string
  onClose: () => void
}

const ORDER: ImportItem['change'][] = ['refused', 'added', 'changed', 'unchanged']

/**
 * Imports a model document into the main model in two steps. The first step sends the file
 * as a preview: the server reads every entry and writes nothing. The dialog then lists what
 * the import adds and changes and what the server refuses. The second step imports, and only
 * when the server refuses no entry and the model did not change after the preview.
 */
export function ImportModelDialog({ projectId, onClose }: Props) {
  const queryClient = useQueryClient()
  const id = useId()
  const [file, setFile] = useState<File | null>(null)
  const [showUnchanged, setShowUnchanged] = useState(false)

  const preview = useMutation({ mutationFn: (chosen: File) => importModel(projectId, chosen, null), onError: shownInline })
  const apply = useMutation({
    mutationFn: (plan: ImportResult) => importModel(projectId, file!, plan.modelVersion),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId] }),
    onError: shownInline,
  })

  const choose = (chosen: File | null) => {
    setFile(chosen)
    apply.reset()
    preview.reset()
    if (chosen) preview.mutate(chosen)
  }

  const plan = preview.data
  const done = apply.data
  const writes = plan ? plan.added + plan.changed : 0
  const remapped = plan?.items.filter((item) => item.importedAs).length ?? 0
  const shown = (plan?.items ?? [])
    .filter((item) => showUnchanged || item.change !== 'unchanged')
    .sort((a, b) => ORDER.indexOf(a.change) - ORDER.indexOf(b.change) || ENTITY_ORDER.indexOf(a.kind) - ENTITY_ORDER.indexOf(b.kind))
  const changedSincePreview = apply.error instanceof ApiError && apply.error.status === 409

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Import a model document</DialogTitle>
          <DialogDescription>
            The import writes to main. It adds the entries that main does not have and changes the entries that main has, by ID. It removes nothing. You see each change before Vellum writes it.
          </DialogDescription>
        </DialogHeader>

        {!done && (
          <div>
            <label htmlFor={`${id}-file`} className="mb-1 block text-xs font-medium text-muted-foreground">Model document (JSON or YAML)</label>
            <input
              id={`${id}-file`} type="file" accept=".json,.yaml,.yml,application/json,application/x-yaml"
              aria-describedby={preview.isError ? `${id}-error` : undefined}
              onChange={(e) => choose(e.target.files?.[0] ?? null)}
              className="block w-full rounded-lg border border-input text-sm text-foreground file:mr-3 file:border-0 file:bg-secondary file:px-3 file:py-1.5 file:text-sm file:text-secondary-foreground focus-visible:outline-2 focus-visible:outline-primary"
            />
          </div>
        )}

        {preview.isPending && <p className="text-xs text-muted-foreground">The server reads the document. Nothing is written.</p>}
        {preview.isError && (
          <div id={`${id}-error`} role="alert" className="text-xs text-destructive">
            <p>The server refuses the document. {preview.error.message}</p>
          </div>
        )}

        {plan && !done && (
          <div className="min-h-0 flex-1 space-y-2 overflow-y-auto pr-1">
            <p className="text-sm text-foreground" data-testid="import-summary">
              {plan.added} to add · {plan.changed} to change · {plan.unchanged} unchanged
              {plan.refused > 0 && <span className="text-destructive"> · {plan.refused} refused</span>}
            </p>
            {plan.refused > 0 && (
              <p role="alert" className="text-xs text-destructive">
                The server refuses {plan.refused} {plan.refused === 1 ? 'entry' : 'entries'}. An import is all or nothing, so nothing can be imported. Correct the document and choose it again.
              </p>
            )}
            {plan.refused === 0 && writes === 0 && <p className="text-xs text-muted-foreground">Main already holds each entry of the document with the same values. There is nothing to import.</p>}
            {remapped > 0 && (
              <p className="text-xs text-muted-foreground">
                {remapped} {remapped === 1 ? 'schema has an ID that belongs' : 'schemas have IDs that belong'} to another project. In this project {remapped === 1 ? 'it gets a new ID' : 'they get new IDs'}, and each reference in the document is written with the new ID. The same document gives the same IDs each time.
              </p>
            )}

            {shown.length > 0 && (
              <div className="overflow-x-auto rounded-md border border-border">
                <table className="w-full text-left text-xs">
                  <thead className="border-b border-border text-muted-foreground">
                    <tr><th className="px-2.5 py-1.5 font-medium">Change</th><th className="px-2.5 py-1.5 font-medium">Kind</th><th className="px-2.5 py-1.5 font-medium">Entry</th></tr>
                  </thead>
                  <tbody>
                    {shown.map((item) => (
                      <tr key={`${item.kind}:${item.id}`} className="border-b border-border align-top last:border-b-0">
                        <td className="px-2.5 py-1.5"><ChangeChip kind={item.change === 'added' ? 'add' : item.change === 'changed' ? 'change' : item.change} /></td>
                        <td className="px-2.5 py-1.5 text-muted-foreground">{ENTITY_LABEL[item.kind]?.replace(/s$/, '') ?? item.kind}</td>
                        <td className="px-2.5 py-1.5">
                          <p className="break-words text-foreground">{item.name || <span className="font-mono text-muted-foreground">{item.id}</span>}</p>
                          {item.error && <p className="mt-0.5 break-words text-destructive">{item.error}</p>}
                          {item.importedAs && <p className="mt-0.5 break-all font-mono text-[11px] text-muted-foreground">new ID {item.importedAs}</p>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {plan.unchanged > 0 && (
              <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
                <input type="checkbox" checked={showUnchanged} onChange={(e) => setShowUnchanged(e.target.checked)} className="accent-[var(--primary)]" />
                Show the {plan.unchanged} unchanged {plan.unchanged === 1 ? 'entry' : 'entries'}
              </label>
            )}
          </div>
        )}

        {done && (
          <p role="status" className="text-sm text-foreground" data-testid="import-done">
            The import is in main: {done.added} added, {done.changed} changed, {done.unchanged} unchanged.
          </p>
        )}
        {apply.isError && (
          <p role="alert" className="text-xs text-destructive">
            Nothing was imported. {describeError(apply.error)}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="ghost" onClick={onClose}>{done ? 'Close' : 'Cancel'}</Button>
          {changedSincePreview && file && <Button type="button" variant="secondary" onClick={() => choose(file)}>Preview again</Button>}
          {plan && !done && !changedSincePreview && (
            <Button type="button" disabled={plan.refused > 0 || writes === 0 || apply.isPending} onClick={() => apply.mutate(plan)}>
              {apply.isPending ? 'Importing…' : `Import ${writes} ${writes === 1 ? 'change' : 'changes'} into main`}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
