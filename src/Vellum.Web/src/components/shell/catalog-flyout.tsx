import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { validKindsForContext } from '@/lib/containment-rules'
import { kindColor } from '@/lib/kind-colors'
import { cn } from '@/lib/utils'

interface CatalogFlyoutProps {
  elements: { id: string; kind: string }[]
  onAddElement: (kind: string) => void
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

export function CatalogFlyout({ elements, onAddElement }: CatalogFlyoutProps) {
  const currentRootId = useCanvasStore((s) => s.currentRootId)
  const closeFlyout = useShellStore((s) => s.closeFlyout)

  const parentKind = currentRootId
    ? elements.find((e) => e.id === currentRootId)?.kind ?? null
    : null

  const kinds = validKindsForContext(parentKind)

  const handleClick = (kind: string) => {
    onAddElement(kind)
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
        {kinds.map((kind) => (
          <button
            key={kind}
            className={cn(
              'flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted'
            )}
            onClick={() => handleClick(kind)}
          >
            <span
              className="h-3 w-3 rounded-full flex-shrink-0"
              style={{ backgroundColor: kindColor(kind) }}
            />
            <span className="flex-1">{KIND_LABELS[kind] ?? kind}</span>
            <kbd className="rounded border border-border bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
              {KIND_SHORTCUTS[kind]}
            </kbd>
          </button>
        ))}
      </div>
    </div>
  )
}
