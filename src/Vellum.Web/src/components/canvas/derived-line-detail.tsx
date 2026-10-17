import { ArrowRight } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'
import type { SelectedDerivedLine } from '@/stores/shell-store'

interface NamedElement {
  id: string
  name: string
  kind: string
}

interface ListedRelationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

/**
 * The relationships under a derived line, for the detail panel. Each entry opens its
 * relationship. The line itself is a projection of the view: it has nothing to edit.
 */
export function DerivedLineDetail({ line, elements, relationships, onSelectElement, onSelectRelationship }: {
  line: SelectedDerivedLine
  elements: NamedElement[]
  relationships: ListedRelationship[]
  onSelectElement: (id: string) => void
  onSelectRelationship: (id: string) => void
}) {
  const byId = new Map(elements.map((e) => [e.id, e]))
  const listed = line.relationshipIds
    .map((id) => relationships.find((r) => r.id === id))
    .filter((r): r is ListedRelationship => r !== undefined)
  const end = (id: string) => {
    const element = byId.get(id)
    if (!element) return <span className="text-muted-foreground">Not shown</span>
    return (
      <button
        type="button"
        onClick={() => onSelectElement(element.id)}
        className="inline-flex min-w-0 items-center gap-1.5 rounded text-foreground underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary"
      >
        <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: kindColor(element.kind) }} />
        <span className="truncate">{element.name}</span>
      </button>
    )
  }
  const name = (id: string) => byId.get(id)?.name ?? 'Not shown'

  return (
    <div className="space-y-4">
      <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
        {end(line.fromId)}
        <ArrowRight aria-label="to" className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
        {end(line.toId)}
      </p>
      <p className="text-xs leading-relaxed text-muted-foreground">
        A derived line joins two cards for the relationships between the parts inside them. It is drawn for this view and is not stored. Select a relationship to open it.
      </p>
      <section aria-labelledby="derived-line-relationships">
        <h3 id="derived-line-relationships" className="mb-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          Relationships <span className="font-mono tabular-nums">{listed.length}</span>
        </h3>
        <ul aria-labelledby="derived-line-relationships" className="divide-y divide-border overflow-hidden rounded-md border border-border">
          {listed.map((r) => (
            <li key={r.id}>
              <button
                type="button"
                onClick={() => onSelectRelationship(r.id)}
                className="block w-full px-2.5 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary"
              >
                <span className="flex min-w-0 items-center gap-1.5 text-sm text-foreground">
                  <span className="min-w-0 truncate">{name(r.fromId)}</span>
                  <ArrowRight aria-label="to" className="h-3 w-3 shrink-0 text-muted-foreground" />
                  <span className="min-w-0 truncate">{name(r.toId)}</span>
                </span>
                {(r.label || r.technology) && (
                  <span className="block truncate text-xs text-muted-foreground">
                    {r.label}
                    {r.label && r.technology && ' · '}
                    {r.technology && <span className="font-mono text-[11px]">{r.technology}</span>}
                  </span>
                )}
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}
