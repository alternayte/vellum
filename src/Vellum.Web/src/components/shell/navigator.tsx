import { useMemo } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { kindColor } from '@/lib/kind-colors'

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

interface NavigatorProps {
  elements: ElementItem[]
  views: ViewItem[]
}

export function Navigator({ elements, views }: NavigatorProps) {
  const { navigatorOpen, selectElement } = useShellStore()
  const { drillInto } = useCanvasStore()

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

      <div className="border-t border-border">
        <div className="flex items-center justify-between px-3 py-2">
          <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
            Views
          </span>
          <button className="text-xs text-primary hover:text-primary/80">+</button>
        </div>
        <div className="px-2 pb-2">
          {views.map((view) => (
            <button
              key={view.id}
              className="w-full rounded px-2 py-1 text-left text-sm hover:bg-muted"
            >
              {view.name}
            </button>
          ))}
        </div>
      </div>
    </aside>
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
