import { useState } from 'react'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { canContain, validKindsForContext } from '@/lib/containment-rules'
import { kindColor } from '@/lib/kind-colors'

interface CatalogFlyoutProps {
  elements: { id: string; kind: string; name: string }[]
  /** `parentId` is the element the new one goes inside; null is the top level. */
  onAddElement: (kind: string, parentId: string | null) => void
}

const KIND_LABELS: Record<string, string> = {
  actor: 'Actor',
  system: 'System',
  app: 'App',
  store: 'Store',
  component: 'Component',
}

const KIND_SHORTCUTS: Record<string, string> = {
  actor: '⇧P',
  system: '⇧S',
  app: '⇧A',
  store: '⇧D',
  component: '⇧X',
}

/**
 * The kinds an element can be, for a chosen place. The place starts as the selected system
 * or app, so its first child is one click away, and otherwise as the level the canvas shows.
 */
export function CatalogFlyout({ elements, onAddElement }: CatalogFlyoutProps) {
  const currentRootId = useCanvasStore((s) => s.currentRootId)
  const selectedElementId = useShellStore((s) => s.selectedElementId)
  const closeFlyout = useShellStore((s) => s.closeFlyout)

  const parents = elements.filter((e) => canContain(e.kind))
  const [parentId, setParentId] = useState(() =>
    parents.some((p) => p.id === selectedElementId) ? selectedElementId : currentRootId)
  const parent = parents.find((p) => p.id === parentId) ?? null
  const kinds = validKindsForContext(parent?.kind ?? null)

  const handleClick = (kind: string) => {
    onAddElement(kind, parent?.id ?? null)
    closeFlyout()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Elements
        </span>
      </div>

      <div className="flex-1 overflow-y-auto p-2">
        <label className="mb-2 block px-1 text-[11px] font-medium text-muted-foreground">
          Add inside
          <select
            value={parent?.id ?? ''}
            onChange={(e) => setParentId(e.target.value || null)}
            className="mt-1 h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            <option value="">Top level</option>
            {parents.map((p) => <option key={p.id} value={p.id}>{p.name} ({p.kind})</option>)}
          </select>
        </label>
        {kinds.map((kind) => (
          <button
            key={kind}
            type="button"
            className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
            onClick={() => handleClick(kind)}
          >
            <span
              className="h-3 w-3 rounded-full flex-shrink-0"
              style={{ backgroundColor: kindColor(kind) }}
            />
            <span className="flex-1">{KIND_LABELS[kind] ?? kind}</span>
            {/* The Shift keys add to the level the canvas shows, so they show for that place only. */}
            {(parent?.id ?? null) === currentRootId && (
              <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                {KIND_SHORTCUTS[kind]}
              </kbd>
            )}
          </button>
        ))}
        <p className="mt-2 px-1 text-[11px] leading-relaxed text-muted-foreground">
          {parent
            ? `The new element goes inside ${parent.name}. Rename it on the canvas: double-click its name.`
            : 'Actors and systems go on the top level. Apps and stores go inside a system, and components inside an app.'}
        </p>
      </div>
    </div>
  )
}
