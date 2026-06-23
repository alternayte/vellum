import { useMemo, useState } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { useDraftStore } from '@/stores/draft-store'
import { kindColor } from '@/lib/kind-colors'
import { DraftList } from '@/components/draft/draft-list'
import { Badge } from '@/components/ui/badge'
import { useComments } from '@/hooks/use-draft-comments'
import { useCreateDoc } from '@/hooks/use-docs'
import { useCreateView } from '@/hooks/use-views'
import { useCreateSpace } from '@/hooks/use-spaces'

interface ElementItem {
  id: string
  kind: string
  name: string
  status: string
  parentId: string | null
  children?: ElementItem[]
}

interface ViewItem {
  id: string
  name: string
}

interface SpaceItem {
  id: string
  name: string
}

interface DocItem {
  id: string
  title: string
  spaceId: string | null
  adrStatus?: string | null
}

interface NavigatorProps {
  projectId: string
  elements: ElementItem[]
  views: ViewItem[]
  spaces?: SpaceItem[]
  docs?: DocItem[]
}

export function Navigator({ projectId, elements, views, spaces = [], docs = [] }: NavigatorProps) {
  const { navigatorOpen, selectElement } = useShellStore()
  const { drillInto } = useCanvasStore()
  const { activeDraftId } = useDraftStore()

  const childrenMap = useMemo(() => {
    const map = new Map<string | null, ElementItem[]>()
    for (const el of elements) {
      const key = el.parentId
      if (!map.has(key)) map.set(key, [])
      map.get(key)!.push(el)
    }
    return map
  }, [elements])

  if (!navigatorOpen) return null

  const topLevel = childrenMap.get(null) ?? []

  return (
    <aside className="flex w-60 flex-col border-r border-border bg-card">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Model
        </span>
      </div>

      <div className="flex-1 overflow-y-auto p-2">
        {topLevel.map((element) => (
          <TreeNode
            key={element.id}
            element={element}
            childrenMap={childrenMap}
            onSelect={selectElement}
            onDrill={drillInto}
            depth={0}
          />
        ))}
      </div>

      <DraftList projectId={projectId} />

      {activeDraftId && (
        <CommentsSection projectId={projectId} draftId={activeDraftId} />
      )}

      <ViewsSection projectId={projectId} views={views} />

      <DocsSection projectId={projectId} spaces={spaces} docs={docs} />
    </aside>
  )
}

const ADR_STATUS_COLORS: Record<string, string> = {
  proposed: 'bg-blue-100 text-blue-700 border-blue-200',
  accepted: 'bg-green-100 text-green-700 border-green-200',
  superseded: 'bg-gray-100 text-gray-500 border-gray-200',
  deprecated: 'bg-amber-100 text-amber-700 border-amber-200',
}

function AdrBadge({ status }: { status: string }) {
  const cls = ADR_STATUS_COLORS[status] ?? 'bg-gray-100 text-gray-500 border-gray-200'
  return (
    <span className={`inline-flex items-center rounded border px-1 py-0 text-[10px] font-medium ${cls}`}>
      {status}
    </span>
  )
}

function DocRow({ doc, indent = false }: { doc: DocItem; indent?: boolean }) {
  const openDoc = (docId: string) => useShellStore.getState().openDoc(docId)
  return (
    <button
      className={`flex w-full items-center gap-1.5 rounded px-2 py-1 text-left text-sm hover:bg-muted ${indent ? 'pl-7' : ''}`}
      onClick={() => openDoc(doc.id)}
    >
      <span className="truncate flex-1">{doc.title}</span>
      {doc.adrStatus && <AdrBadge status={doc.adrStatus} />}
    </button>
  )
}

function CommentsSection({ projectId, draftId }: { projectId: string; draftId: string }) {
  const { data } = useComments(projectId, draftId)
  const count = data?.items.length ?? 0

  return (
    <div className="border-t border-border">
      <div className="flex items-center justify-between px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Comments
        </span>
        {count > 0 && (
          <Badge variant="secondary" className="text-xs h-4 px-1.5">
            {count}
          </Badge>
        )}
      </div>
    </div>
  )
}

function ViewsSection({ projectId, views }: { projectId: string; views: ViewItem[] }) {
  const { activeViewId, setActiveView } = useShellStore()
  const createView = useCreateView(projectId)

  const handleCreateView = () => {
    const name = window.prompt('View name')
    if (name?.trim()) {
      const id = crypto.randomUUID()
      createView.mutate({ id, name: name.trim() })
    }
  }

  return (
    <div className="border-t border-border">
      <div className="flex items-center justify-between px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Views
        </span>
        <button
          className="text-xs text-primary hover:text-primary/80"
          onClick={handleCreateView}
        >
          +
        </button>
      </div>
      <div className="px-2 pb-2">
        {views.map((view) => (
          <button
            key={view.id}
            className={`w-full rounded px-2 py-1 text-left text-sm hover:bg-muted ${
              activeViewId === view.id ? 'bg-accent text-accent-foreground' : ''
            }`}
            onClick={() => setActiveView(view.id)}
          >
            {view.name}
          </button>
        ))}
      </div>
    </div>
  )
}

function DocsSection({ projectId, spaces, docs }: { projectId: string; spaces: SpaceItem[]; docs: DocItem[] }) {
  const [expandedSpaces, setExpandedSpaces] = useState<Set<string>>(new Set())
  const { activeDraftId } = useDraftStore()
  const createDoc = useCreateDoc(projectId)
  const createSpace = useCreateSpace(projectId)

  const toggleSpace = (spaceId: string) => {
    setExpandedSpaces((prev) => {
      const next = new Set(prev)
      if (next.has(spaceId)) {
        next.delete(spaceId)
      } else {
        next.add(spaceId)
      }
      return next
    })
  }

  const handleNewDoc = () => {
    const title = window.prompt('Document title')
    if (title?.trim()) {
      const id = crypto.randomUUID()
      createDoc.mutate({ id, title: title.trim() }, {
        onSuccess: () => useShellStore.getState().openDoc(id),
      })
    }
  }

  const handleNewSpace = () => {
    const name = window.prompt('Space name')
    if (name?.trim()) {
      createSpace.mutate({ id: crypto.randomUUID(), name: name.trim() })
    }
  }

  const handleNewAdr = () => {
    const id = crypto.randomUUID()
    createDoc.mutate({
      id,
      title: 'New ADR',
      draftId: activeDraftId ?? undefined,
      adrStatus: 'proposed',
    }, {
      onSuccess: () => useShellStore.getState().openDoc(id),
    })
  }

  const looseDocs = docs.filter((d) => d.spaceId === null)

  return (
    <div className="border-t border-border">
      <div className="flex items-center justify-between px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Docs
        </span>
        <div className="flex items-center gap-2">
          <button
            className="text-xs text-muted-foreground hover:text-foreground"
            onClick={handleNewSpace}
            title="New space"
          >
            Space+
          </button>
          {activeDraftId && (
            <button
              className="text-xs text-primary hover:text-primary/80 disabled:opacity-50"
              onClick={handleNewAdr}
              disabled={createDoc.isPending}
              title="New ADR"
            >
              ADR+
            </button>
          )}
          <button
            className="text-xs text-primary hover:text-primary/80 disabled:opacity-50"
            onClick={handleNewDoc}
            disabled={createDoc.isPending}
            title="New document"
          >
            +
          </button>
        </div>
      </div>
      <div className="px-2 pb-2">
        {spaces.map((space) => {
          const spaceDocs = docs.filter((d) => d.spaceId === space.id)
          const isExpanded = expandedSpaces.has(space.id)
          return (
            <div key={space.id}>
              <button
                className="flex w-full items-center gap-1 rounded px-2 py-1 text-left text-sm hover:bg-muted"
                onClick={() => toggleSpace(space.id)}
              >
                <span className="text-muted-foreground">{isExpanded ? '▾' : '▸'}</span>
                <span className="truncate">{space.name}</span>
              </button>
              {isExpanded && (
                <div>
                  {spaceDocs.map((doc) => (
                    <DocRow key={doc.id} doc={doc} indent />
                  ))}
                  {spaceDocs.length === 0 && (
                    <p className="px-7 py-1 text-xs text-muted-foreground">No docs</p>
                  )}
                </div>
              )}
            </div>
          )
        })}

        {looseDocs.length > 0 && (
          <div>
            {spaces.length > 0 && (
              <p className="px-2 py-1 text-xs text-muted-foreground">Unsorted</p>
            )}
            {looseDocs.map((doc) => (
              <DocRow key={doc.id} doc={doc} />
            ))}
          </div>
        )}

        {docs.length === 0 && spaces.length === 0 && (
          <p className="px-2 py-1 text-xs text-muted-foreground">No docs yet</p>
        )}
      </div>
    </div>
  )
}

function TreeNode({
  element,
  childrenMap,
  onSelect,
  onDrill,
  depth,
}: {
  element: ElementItem
  childrenMap: Map<string | null, ElementItem[]>
  onSelect: (id: string) => void
  onDrill: (e: { elementId: string; name: string; kind: string }) => void
  depth: number
}) {
  const children = childrenMap.get(element.id) ?? []

  return (
    <div>
      <button
        className="flex w-full items-center gap-2 rounded px-2 py-1 text-sm hover:bg-muted"
        style={{ paddingLeft: `${depth * 16 + 8}px` }}
        onClick={() => onSelect(element.id)}
        onDoubleClick={() => {
          if (children.length > 0) {
            onDrill({ elementId: element.id, name: element.name, kind: element.kind })
          }
        }}
      >
        <span
          className="h-2 w-2 rounded-full"
          style={{ backgroundColor: kindColor(element.kind) }}
        />
        <span className="truncate">{element.name}</span>
      </button>
      {children.map((child) => (
        <TreeNode
          key={child.id}
          element={child}
          childrenMap={childrenMap}
          onSelect={onSelect}
          onDrill={onDrill}
          depth={depth + 1}
        />
      ))}
    </div>
  )
}
