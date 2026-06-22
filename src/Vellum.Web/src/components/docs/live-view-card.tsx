// src/Vellum.Web/src/components/docs/live-view-card.tsx
import { useShellStore } from '@/stores/shell-store'
import { useDocEditorStore } from '@/stores/doc-editor-store'

interface LiveViewCardProps {
  id: string
  views: { id: string; name: string }[]
  elementCount?: number
}

export function LiveViewCard({ id, views, elementCount }: LiveViewCardProps) {
  const view = views.find((v) => v.id === id)

  if (!view) {
    return (
      <div className="my-2 rounded-lg border border-border bg-muted/50 p-4 text-sm text-muted-foreground">
        View not found
      </div>
    )
  }

  return (
    <button
      className="my-2 flex w-full items-center gap-3 rounded-lg border border-border bg-card p-4 text-left hover:bg-muted"
      onClick={() => {
        useDocEditorStore.getState().closeDoc()
        useShellStore.getState().setActiveView(id)
      }}
    >
      <div className="flex h-10 w-10 items-center justify-center rounded bg-primary/10 text-primary">
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
          <rect x="1" y="1" width="14" height="14" rx="2" />
          <circle cx="5" cy="6" r="1.5" />
          <circle cx="11" cy="6" r="1.5" />
          <line x1="5" y1="6" x2="11" y2="6" />
          <circle cx="8" cy="11" r="1.5" />
          <line x1="8" y1="9.5" x2="8" y2="7.5" />
        </svg>
      </div>
      <div>
        <div className="text-sm font-medium">{view.name}</div>
        {elementCount !== undefined && (
          <div className="text-xs text-muted-foreground">{elementCount} elements</div>
        )}
      </div>
    </button>
  )
}
