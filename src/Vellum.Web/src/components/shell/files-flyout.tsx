import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { useShellStore } from '@/stores/shell-store'
import { Download, FileText, LayoutDashboard, Upload } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { ImportModelDialog } from '@/components/branch/import-model-dialog'
import { downloadModel } from '@/lib/branches-api'
import { shownInline } from '@/lib/mutation-errors'

interface ViewItem {
  id: string
  name: string
}

interface DocItem {
  id: string
  title: string
  type?: string | null
}

interface FilesFlyoutProps {
  views: ViewItem[]
  docs: DocItem[]
  /** The project whose model document the flyout exports and imports. Without it the section is not shown. */
  model?: { projectId: string; projectName: string; canImport: boolean }
}

const TYPE_COLORS: Record<string, string> = {
  prd: 'border-[var(--trace-event)]/50 text-[var(--trace-event)]',
  sdd: 'border-[var(--trace-request)]/50 text-[var(--trace-request)]',
}

/** Exports the model document of main as a file and opens the import of one. */
function ModelDocument({ projectId, projectName, canImport }: NonNullable<FilesFlyoutProps['model']>) {
  const [importing, setImporting] = useState(false)
  const download = useMutation({
    mutationFn: (format: 'json' | 'yaml') => downloadModel(projectId, projectName, format),
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

export function FilesFlyout({ views, docs, model }: FilesFlyoutProps) {
  const activeViewId = useShellStore((s) => s.activeViewId)
  const activeDocId = useShellStore((s) => s.activeDocId)
  const setActiveView = useShellStore((s) => s.setActiveView)
  const openDoc = useShellStore((s) => s.openDoc)
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const [filter, setFilter] = useState('')

  const q = filter.toLowerCase()

  const filteredViews = views.filter((v) => v.name.toLowerCase().includes(q))
  const filteredDocs = docs.filter((d) => d.title.toLowerCase().includes(q))

  const handleSelectView = (id: string) => {
    setActiveView(id)
    closeFlyout()
  }

  const handleSelectDoc = (id: string) => {
    openDoc(id)
    closeFlyout()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Files
        </span>
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
          <button
            key={view.id}
            className={cn(
              'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted',
              activeViewId === view.id && 'bg-accent text-accent-foreground'
            )}
            onClick={() => handleSelectView(view.id)}
          >
            <LayoutDashboard className="h-3.5 w-3.5 flex-shrink-0 text-muted-foreground" />
            <span className="truncate">{view.name}</span>
          </button>
        ))}

        {filteredDocs.map((doc) => (
          <button
            key={doc.id}
            className={cn(
              'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted',
              activeDocId === doc.id && 'bg-accent text-accent-foreground'
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
        ))}

        {filteredViews.length === 0 && filteredDocs.length === 0 && (
          <p className="px-2 py-4 text-center text-xs text-muted-foreground">
            {filter ? 'No matches' : 'No files yet'}
          </p>
        )}
      </div>

      {model && <ModelDocument {...model} />}
    </div>
  )
}
