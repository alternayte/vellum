// src/Vellum.Web/src/components/docs/element-link.tsx
import { kindColor } from '@/lib/kind-colors'
import { useShellStore } from '@/stores/shell-store'

interface ElementLinkProps {
  id: string
  elements: { id: string; kind: string; name: string }[]
}

export function ElementLink({ id, elements }: ElementLinkProps) {
  const element = elements.find((e) => e.id === id)

  if (!element) {
    return (
      <span className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/50 px-2 py-0.5 text-xs text-muted-foreground">
        Element removed
      </span>
    )
  }

  return (
    <button
      className="inline-flex items-center gap-1 rounded-full border border-border bg-card px-2 py-0.5 text-xs hover:bg-muted"
      onClick={() => useShellStore.getState().selectElement(id)}
    >
      <span
        className="h-2 w-2 rounded-full"
        style={{ backgroundColor: kindColor(element.kind) }}
      />
      <span className="font-medium">{element.name}</span>
    </button>
  )
}
