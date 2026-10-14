import { useState } from 'react'
import { FileText, Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { TextPromptDialog } from '@/components/shell/text-prompt-dialog'
import { useCreateDoc, useDocs } from '@/hooks/use-docs'
import { useProject } from '@/hooks/use-model'
import { useDraftStore } from '@/stores/draft-store'
import { useShellStore } from '@/stores/shell-store'
import { canEditModel } from '@/lib/branches-api'

const HEADING = 'mb-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground'

/** The documents attached to an element, in the element detail panel, and a new one for it. */
export function ElementDocs({ projectId, element }: { projectId: string; element: { id: string; name: string } }) {
  const { data: docs, isLoading, error } = useDocs(projectId, { elementId: element.id })
  const { data: project } = useProject(projectId)
  const createDoc = useCreateDoc(projectId)
  const [naming, setNaming] = useState(false)
  const canEdit = project !== undefined && canEditModel(project.yourRole)
  const headingId = `element-docs-${element.id}`

  return (
    <section aria-labelledby={headingId}>
      <h3 id={headingId} className={HEADING}>Docs</h3>
      {isLoading ? (
        <p className="text-xs text-muted-foreground">Loading documents…</p>
      ) : error ? (
        <p role="alert" className="text-xs text-destructive">{error.message}</p>
      ) : !docs?.length ? (
        <p className="text-xs leading-relaxed text-muted-foreground">No document is attached to this element.</p>
      ) : (
        <ul aria-labelledby={headingId} className="divide-y divide-border overflow-hidden rounded-md border border-border">
          {docs.map((doc) => (
            <li key={doc.id}>
              <button
                type="button"
                className="flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-sm text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary"
                onClick={() => useShellStore.getState().openDoc(doc.id)}
              >
                <FileText aria-hidden="true" className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <span className="min-w-0 flex-1 truncate">{doc.title}</span>
                {doc.type && <span className="font-mono text-[10px] uppercase text-muted-foreground">{doc.type}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
      {canEdit && (
        <Button variant="outline" size="sm" className="mt-2 h-7 gap-1 px-2 text-xs" onClick={() => { createDoc.reset(); setNaming(true) }}>
          <Plus className="h-3 w-3" aria-hidden="true" />
          New doc for this element
        </Button>
      )}
      {naming && (
        <TextPromptDialog
          title="New doc for this element"
          label="Title"
          initial={`${element.name} notes`}
          submitLabel="Create"
          pending={createDoc.isPending}
          error={createDoc.error?.message}
          onSubmit={(title) => {
            const id = crypto.randomUUID()
            const draftId = useDraftStore.getState().activeDraftId
            createDoc.mutate(
              { id, title, elementId: element.id, draftId: draftId ?? undefined },
              { onSuccess: () => { setNaming(false); useShellStore.getState().openDoc(id) } },
            )
          }}
          onClose={() => setNaming(false)}
        />
      )}
    </section>
  )
}
