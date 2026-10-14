import { useState, type DragEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useShellStore } from '@/stores/shell-store'
import { ChevronDown, ChevronRight, Download, FileCode, FileImage, FileText, Folder, FolderPlus, LayoutDashboard, Pencil, Trash2, Upload } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { ActionMenu } from '@/components/ui/action-menu'
import { ImportModelDialog } from '@/components/branch/import-model-dialog'
import { downloadModel } from '@/lib/branches-api'
import { downloadStructurizr, downloadView, type ViewExportFormat } from '@/lib/diagram-export'
import { shownInline } from '@/lib/mutation-errors'
import { DocActions } from '@/components/docs/doc-actions'
import { useDocs, useUpdateDoc } from '@/hooks/use-docs'
import { useCreateSpace, useDeleteSpace, useUpdateSpace } from '@/hooks/use-spaces'
import { useDeleteView, useRenameView } from '@/hooks/use-views'
import { TextPromptDialog } from './text-prompt-dialog'
import { ConfirmDialog } from './confirm-dialog'

interface ViewItem {
  id: string
  name: string
}

interface DocItem {
  id: string
  title: string
  type?: string | null
  spaceId: string | null
  elementId: string | null
}

interface FilesFlyoutProps {
  projectId: string
  views: ViewItem[]
  docs: DocItem[]
  spaces: { id: string; name: string }[]
  /** The elements a document can be attached to. */
  elements: { id: string; name: string; kind: string }[]
  /** False for a viewer: the flyout opens files and changes none. */
  canEdit: boolean
  /** The project whose model document the flyout exports and imports. Without it the section is not shown. */
  model?: { projectId: string; projectName: string; canImport: boolean }
}

const TYPE_COLORS: Record<string, string> = {
  prd: 'border-[var(--trace-event)]/50 text-[var(--trace-event)]',
  sdd: 'border-[var(--trace-request)]/50 text-[var(--trace-request)]',
}

/** The drag data of a document row. */
const DOC_DRAG = 'application/x-vellum-doc'

/** Exports the model document of main as a file and opens the import of one. */
function ModelDocument({ projectId, projectName, canImport }: NonNullable<FilesFlyoutProps['model']>) {
  const [importing, setImporting] = useState(false)
  const download = useMutation({
    mutationFn: (format: 'json' | 'yaml' | 'structurizr') =>
      format === 'structurizr' ? downloadStructurizr(projectId, projectName) : downloadModel(projectId, projectName, format),
    onError: shownInline,
  })

  return (
    <section aria-labelledby="files-model-document" className="border-t border-border px-3 py-3">
      <h3 id="files-model-document" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Model document</h3>
      <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
        One file with the elements, relationships, messages, schemas and contracts of main. It does not hold a draft, a variant, the views or the cost sheet.
      </p>
      <div className="mt-2 flex gap-1.5">
        <Button size="sm" variant="secondary" className="flex-1 gap-1.5" disabled={download.isPending} onClick={() => download.mutate('json')}>
          <Download className="h-3.5 w-3.5" aria-hidden="true" /> Export JSON
        </Button>
        <Button size="sm" variant="secondary" className="flex-1 gap-1.5" disabled={download.isPending} onClick={() => download.mutate('yaml')}>
          <Download className="h-3.5 w-3.5" aria-hidden="true" /> Export YAML
        </Button>
      </div>
      <Button size="sm" variant="secondary" className="mt-1.5 w-full gap-1.5" disabled={download.isPending} onClick={() => download.mutate('structurizr')}>
        <FileCode className="h-3.5 w-3.5" aria-hidden="true" /> Export Structurizr DSL
      </Button>
      <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
        Structurizr DSL holds the elements, relationships and views. It drops truth labels, layout, messages, contracts, scenarios, promises and costs, and cannot be imported back.
      </p>
      {download.isError && <p role="alert" className="mt-1.5 text-xs text-destructive">{download.error.message}</p>}
      {canImport && (
        <Button size="sm" variant="ghost" className="mt-1.5 w-full gap-1.5" onClick={() => setImporting(true)}>
          <Upload className="h-3.5 w-3.5" aria-hidden="true" /> Import a document…
        </Button>
      )}
      {importing && <ImportModelDialog projectId={projectId} onClose={() => setImporting(false)} />}
    </section>
  )
}

/** The rename and delete of a diagram. The delete lists the documents that embed it and still deletes. */
function ViewDialogs({ projectId, view, mode, views, onClose }: {
  projectId: string
  view: ViewItem
  mode: 'rename' | 'delete'
  views: ViewItem[]
  onClose: () => void
}) {
  const rename = useRenameView(projectId)
  const remove = useDeleteView(projectId)
  const embedding = useDocs(projectId, { embedsViewId: view.id }, { enabled: mode === 'delete' })

  if (mode === 'rename') {
    return (
      <TextPromptDialog
        title="Rename diagram"
        label="Name"
        initial={view.name}
        submitLabel="Rename"
        pending={rename.isPending}
        error={rename.error?.message}
        onSubmit={(name) => rename.mutate({ id: view.id, name }, { onSuccess: onClose })}
        onClose={onClose}
      />
    )
  }

  const docs = embedding.data ?? []
  return (
    <ConfirmDialog
      title="Delete diagram"
      description={<>“{view.name}” and its layout are deleted. The elements and relationships stay in the model.</>}
      confirmLabel="Delete diagram"
      destructive
      pending={remove.isPending || embedding.isLoading}
      error={remove.error?.message ?? embedding.error?.message}
      onConfirm={() => remove.mutate(view.id, {
        onSuccess: () => {
          const shell = useShellStore.getState()
          if (shell.activeViewId === view.id) shell.setActiveView(views.find((v) => v.id !== view.id)?.id ?? null)
          onClose()
        },
      })}
      onClose={onClose}
    >
      {embedding.isLoading ? (
        <p className="text-xs text-muted-foreground">Looking for documents that embed it…</p>
      ) : docs.length === 0 ? (
        <p className="text-xs text-muted-foreground">No document embeds this diagram.</p>
      ) : (
        <div className="text-xs text-muted-foreground">
          <p>
            {docs.length === 1 ? 'One document embeds this diagram. Its text stays as it is, and its embed shows' : `${docs.length} documents embed this diagram. Their text stays as it is, and their embeds show`} “This diagram was deleted.”
          </p>
          <ul className="mt-1.5 max-h-40 list-disc space-y-0.5 overflow-y-auto pl-5 text-foreground">
            {docs.map((d) => <li key={d.id}>{d.title}</li>)}
          </ul>
        </div>
      )}
    </ConfirmDialog>
  )
}

export function FilesFlyout({ projectId, views, docs, spaces, elements, canEdit, model }: FilesFlyoutProps) {
  const activeViewId = useShellStore((s) => s.activeViewId)
  const activeDocId = useShellStore((s) => s.activeDocId)
  const setActiveView = useShellStore((s) => s.setActiveView)
  const openDoc = useShellStore((s) => s.openDoc)
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const [filter, setFilter] = useState('')
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [viewDialog, setViewDialog] = useState<{ view: ViewItem; mode: 'rename' | 'delete' } | null>(null)
  const [spaceDialog, setSpaceDialog] = useState<{ mode: 'new' } | { mode: 'rename' | 'delete'; space: { id: string; name: string } } | null>(null)
  const [dropTarget, setDropTarget] = useState<string | null>(null)
  const moveDoc = useUpdateDoc(projectId)
  const exportView = useMutation({
    mutationFn: ({ view, format }: { view: ViewItem; format: ViewExportFormat }) => downloadView(projectId, view.id, view.name, format),
    onError: shownInline,
  })
  const createSpace = useCreateSpace(projectId)
  const renameSpace = useUpdateSpace(projectId)
  const deleteSpace = useDeleteSpace(projectId)

  const q = filter.toLowerCase()
  const filteredViews = views.filter((v) => v.name.toLowerCase().includes(q))
  const filteredDocs = docs.filter((d) => d.title.toLowerCase().includes(q))
  const knownSpace = new Set(spaces.map((s) => s.id))
  const topDocs = filteredDocs.filter((d) => !d.spaceId || !knownSpace.has(d.spaceId))
  const sortedSpaces = [...spaces].sort((a, b) => a.name.localeCompare(b.name))

  const handleSelectView = (id: string) => {
    setActiveView(id)
    closeFlyout()
  }

  const handleSelectDoc = (id: string) => {
    openDoc(id)
    closeFlyout()
  }

  // A document dragged onto a space moves into it; onto the top level, out of its space.
  const dropProps = (spaceId: string | null) => canEdit ? {
    onDragOver: (e: DragEvent) => {
      if (!e.dataTransfer.types.includes(DOC_DRAG)) return
      e.preventDefault()
      e.dataTransfer.dropEffect = 'move'
      setDropTarget(spaceId ?? 'top')
    },
    onDragLeave: (e: DragEvent) => {
      if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDropTarget(null)
    },
    onDrop: (e: DragEvent) => {
      e.preventDefault()
      setDropTarget(null)
      const id = e.dataTransfer.getData(DOC_DRAG)
      const doc = docs.find((d) => d.id === id)
      if (doc && (doc.spaceId ?? null) !== spaceId) moveDoc.mutate({ id, spaceId })
    },
  } : {}

  const docRow = (doc: DocItem, nested: boolean) => (
    <div
      key={doc.id}
      draggable={canEdit}
      onDragStart={(e) => {
        e.dataTransfer.setData(DOC_DRAG, doc.id)
        e.dataTransfer.effectAllowed = 'move'
      }}
      className={cn('group flex items-center rounded-md hover:bg-muted', nested && 'ml-4')}
    >
      <button
        className={cn(
          'flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm',
          activeDocId === doc.id && 'bg-accent text-accent-foreground',
        )}
        onClick={() => handleSelectDoc(doc.id)}
      >
        <FileText className="h-3.5 w-3.5 flex-shrink-0 text-muted-foreground" />
        <span className="flex-1 truncate">{doc.title}</span>
        {doc.type && (
          <span className={cn(
            'inline-flex items-center rounded border px-1 py-0 text-[10px] font-medium uppercase',
            TYPE_COLORS[doc.type] ?? 'border-border text-muted-foreground'
          )}>
            {doc.type}
          </span>
        )}
      </button>
      {canEdit && (
        <DocActions
          projectId={projectId}
          doc={doc}
          spaces={spaces}
          elements={elements}
          className="mr-1 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100"
        />
      )}
    </div>
  )

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Files
        </span>
        {canEdit && (
          <button
            type="button"
            className="flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-muted-foreground hover:bg-muted hover:text-foreground"
            onClick={() => { createSpace.reset(); setSpaceDialog({ mode: 'new' }) }}
          >
            <FolderPlus className="h-3.5 w-3.5" aria-hidden="true" /> New space
          </button>
        )}
      </div>

      <div className="px-3 py-2">
        <input
          type="text"
          aria-label="Filter files"
          placeholder="Filter..."
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          className="w-full rounded-md border border-input bg-background px-2 py-1 text-sm"
        />
      </div>

      <div className="flex-1 overflow-y-auto px-2 pb-2">
        {filteredViews.map((view) => (
          <div
            key={view.id}
            className="group flex items-center rounded-md hover:bg-muted"
          >
            <button
              className={cn(
                'flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm',
                activeViewId === view.id && 'bg-accent text-accent-foreground',
              )}
              onClick={() => handleSelectView(view.id)}
            >
              <LayoutDashboard className="h-3.5 w-3.5 flex-shrink-0 text-muted-foreground" />
              <span className="truncate">{view.name}</span>
            </button>
            <ActionMenu
              label={`Actions for diagram ${view.name}`}
              className="mr-1 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100"
              items={[
                { label: 'Export SVG', icon: <FileImage className="h-3.5 w-3.5" aria-hidden="true" />, disabled: exportView.isPending, onSelect: () => exportView.mutate({ view, format: 'svg' }) },
                { label: 'Export PNG', icon: <FileImage className="h-3.5 w-3.5" aria-hidden="true" />, disabled: exportView.isPending, onSelect: () => exportView.mutate({ view, format: 'png' }) },
                { label: 'Export Mermaid', icon: <FileCode className="h-3.5 w-3.5" aria-hidden="true" />, disabled: exportView.isPending, onSelect: () => exportView.mutate({ view, format: 'mermaid' }) },
                ...(canEdit ? [
                  { label: 'Rename', icon: <Pencil className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => setViewDialog({ view, mode: 'rename' }) },
                  { label: 'Delete…', icon: <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />, destructive: true, onSelect: () => setViewDialog({ view, mode: 'delete' }) },
                ] : []),
              ]}
            />
          </div>
        ))}

        {sortedSpaces.map((space) => {
          const inside = filteredDocs.filter((d) => d.spaceId === space.id)
          if (q && inside.length === 0 && !space.name.toLowerCase().includes(q)) return null
          const open = !collapsed.has(space.id) || q.length > 0
          return (
            <div
              key={space.id}
              role="group"
              aria-label={`Space ${space.name}`}
              className={cn('rounded-md', dropTarget === space.id && 'bg-primary/10 ring-1 ring-primary/60')}
              {...dropProps(space.id)}
            >
              <div className="group flex items-center rounded-md hover:bg-muted">
                <button
                  type="button"
                  aria-expanded={open}
                  className="flex min-w-0 flex-1 items-center gap-1.5 px-2 py-1.5 text-left text-sm"
                  onClick={() => setCollapsed((c) => {
                    const next = new Set(c)
                    if (next.has(space.id)) next.delete(space.id)
                    else next.add(space.id)
                    return next
                  })}
                >
                  {open ? <ChevronDown className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" /> : <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />}
                  <Folder className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="flex-1 truncate">{space.name}</span>
                  <span className="font-mono text-[10px] tabular-nums text-muted-foreground">{docs.filter((d) => d.spaceId === space.id).length}</span>
                </button>
                {canEdit && (
                  <ActionMenu
                    label={`Actions for space ${space.name}`}
                    className="mr-1 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100"
                    items={[
                      { label: 'Rename', icon: <Pencil className="h-3.5 w-3.5" aria-hidden="true" />, onSelect: () => { renameSpace.reset(); setSpaceDialog({ mode: 'rename', space }) } },
                      { label: 'Delete…', icon: <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />, destructive: true, onSelect: () => { deleteSpace.reset(); setSpaceDialog({ mode: 'delete', space }) } },
                    ]}
                  />
                )}
              </div>
              {open && inside.map((doc) => docRow(doc, true))}
              {open && inside.length === 0 && !q && (
                <p className="ml-6 px-2 py-1 text-[11px] text-muted-foreground">{canEdit ? 'Empty. Drag a document here.' : 'Empty'}</p>
              )}
            </div>
          )
        })}

        <div
          className={cn('min-h-6 rounded-md', dropTarget === 'top' && 'bg-primary/10 ring-1 ring-primary/60')}
          aria-label="Documents with no space"
          {...dropProps(null)}
        >
          {topDocs.map((doc) => docRow(doc, false))}
        </div>

        {moveDoc.isError && <p role="alert" className="px-2 py-1 text-xs text-destructive">{moveDoc.error.message}</p>}
        {exportView.isError && <p role="alert" className="px-2 py-1 text-xs text-destructive">{exportView.error.message}</p>}

        {filteredViews.length === 0 && filteredDocs.length === 0 && spaces.length === 0 && (
          <p className="px-2 py-4 text-center text-xs text-muted-foreground">
            {filter ? 'No matches' : 'No files yet'}
          </p>
        )}
      </div>

      {model && <ModelDocument {...model} />}

      {viewDialog && (
        <ViewDialogs projectId={projectId} view={viewDialog.view} mode={viewDialog.mode} views={views} onClose={() => setViewDialog(null)} />
      )}
      {spaceDialog?.mode === 'new' && (
        <TextPromptDialog
          title="New space"
          label="Name"
          initial=""
          submitLabel="Create"
          pending={createSpace.isPending}
          error={createSpace.error?.message}
          onSubmit={(name) => createSpace.mutate({ id: crypto.randomUUID(), name }, { onSuccess: () => setSpaceDialog(null) })}
          onClose={() => setSpaceDialog(null)}
        />
      )}
      {spaceDialog?.mode === 'rename' && (
        <TextPromptDialog
          title="Rename space"
          label="Name"
          initial={spaceDialog.space.name}
          submitLabel="Rename"
          pending={renameSpace.isPending}
          error={renameSpace.error?.message}
          onSubmit={(name) => renameSpace.mutate({ id: spaceDialog.space.id, name }, { onSuccess: () => setSpaceDialog(null) })}
          onClose={() => setSpaceDialog(null)}
        />
      )}
      {spaceDialog?.mode === 'delete' && (
        <ConfirmDialog
          title="Delete space"
          description={(() => {
            const count = docs.filter((d) => d.spaceId === spaceDialog.space.id).length
            return <>“{spaceDialog.space.name}” is deleted. {count === 0 ? 'It holds no document.' : `Its ${count === 1 ? 'document moves' : `${count} documents move`} to the top level and ${count === 1 ? 'is' : 'are'} not deleted.`}</>
          })()}
          confirmLabel="Delete space"
          destructive
          pending={deleteSpace.isPending}
          error={deleteSpace.error?.message}
          onConfirm={() => deleteSpace.mutate(spaceDialog.space.id, { onSuccess: () => setSpaceDialog(null) })}
          onClose={() => setSpaceDialog(null)}
        />
      )}
    </div>
  )
}
