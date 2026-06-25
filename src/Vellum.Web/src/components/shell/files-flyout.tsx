import { useState } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { FileText, LayoutDashboard } from 'lucide-react'
import { cn } from '@/lib/utils'

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
}

const TYPE_COLORS: Record<string, string> = {
  adr: 'bg-blue-100 text-blue-700 border-blue-200',
  prd: 'bg-purple-100 text-purple-700 border-purple-200',
  sdd: 'bg-emerald-100 text-emerald-700 border-emerald-200',
}

export function FilesFlyout({ views, docs }: FilesFlyoutProps) {
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
          placeholder="Filter..."
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          className="w-full rounded-md border border-border bg-background px-2 py-1 text-sm focus:outline-none focus:ring-1 focus:ring-ring"
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
                TYPE_COLORS[doc.type] ?? 'bg-gray-100 text-gray-500 border-gray-200'
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
    </div>
  )
}
