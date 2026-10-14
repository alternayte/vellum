import { useState } from 'react'
import { FolderInput, Link2, Pencil, Trash2 } from 'lucide-react'
import { ActionMenu } from '@/components/ui/action-menu'
import { TextPromptDialog } from '@/components/shell/text-prompt-dialog'
import { PickDialog } from '@/components/shell/pick-dialog'
import { ConfirmDialog } from '@/components/shell/confirm-dialog'
import { useDeleteDoc, useUpdateDoc } from '@/hooks/use-docs'
import { useShellStore } from '@/stores/shell-store'

export interface DocRef {
  id: string
  title: string
  spaceId: string | null
  elementId: string | null
}

interface DocActionsProps {
  projectId: string
  doc: DocRef
  spaces: { id: string; name: string }[]
  elements: { id: string; name: string; kind: string }[]
  className?: string
}

type Open = 'rename' | 'move' | 'attach' | 'delete' | null

/** The "⋯" menu of a document: Rename, Move to space…, Attach to element…, Delete. */
export function DocActions({ projectId, doc, spaces, elements, className }: DocActionsProps) {
  const [open, setOpen] = useState<Open>(null)
  const update = useUpdateDoc(projectId)
  const remove = useDeleteDoc(projectId)
  const close = () => setOpen(null)
  const start = (next: Open) => { update.reset(); remove.reset(); setOpen(next) }
  const save = (fields: Parameters<typeof update.mutate>[0]) => update.mutate(fields, { onSuccess: close })

  return (
    <>
      <ActionMenu
        label={`Actions for document ${doc.title}`}
        className={className}
        items={[
          { label: 'Rename', icon: <Pencil className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => start('rename') },
          { label: 'Move to space…', icon: <FolderInput className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => start('move') },
          { label: 'Attach to element…', icon: <Link2 className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => start('attach') },
          { label: 'Delete…', icon: <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />, destructive: true, onSelect: () => start('delete') },
        ]}
      />
      {open === 'rename' && (
        <TextPromptDialog
          title="Rename document"
          label="Title"
          initial={doc.title}
          submitLabel="Rename"
          pending={update.isPending}
          error={update.error?.message}
          onSubmit={(title) => save({ id: doc.id, title })}
          onClose={close}
        />
      )}
      {open === 'move' && (
        <PickDialog
          title="Move to space"
          options={[{ value: null, label: 'No space (top level)' }, ...spaces.map((s) => ({ value: s.id, label: s.name }))]}
          current={doc.spaceId}
          submitLabel="Move"
          pending={update.isPending}
          error={update.error?.message}
          onSubmit={(spaceId) => save({ id: doc.id, spaceId })}
          onClose={close}
        />
      )}
      {open === 'attach' && (
        <PickDialog
          title="Attach to element"
          options={[
            { value: null, label: 'No element' },
            ...[...elements].sort((a, b) => a.name.localeCompare(b.name)).map((e) => ({ value: e.id, label: e.name, hint: e.kind })),
          ]}
          current={doc.elementId}
          submitLabel="Attach"
          pending={update.isPending}
          error={update.error?.message}
          onSubmit={(elementId) => save({ id: doc.id, elementId })}
          onClose={close}
        />
      )}
      {open === 'delete' && (
        <ConfirmDialog
          title="Delete document"
          description={<>“{doc.title}” and its text are deleted. This cannot be undone.</>}
          confirmLabel="Delete document"
          destructive
          pending={remove.isPending}
          error={remove.error?.message}
          onConfirm={() => remove.mutate(doc.id, {
            onSuccess: () => {
              if (useShellStore.getState().activeDocId === doc.id) useShellStore.getState().closeDoc()
              close()
            },
          })}
          onClose={close}
        />
      )}
    </>
  )
}
