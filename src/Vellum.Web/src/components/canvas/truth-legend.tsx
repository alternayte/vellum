import { Panel } from '@xyflow/react'
import { factStatusColor, factStatusLabel } from '@/lib/fact-status'

interface TruthLegendProps {
  factStatuses: string[]
  relationshipKinds: string[]
  onClose: () => void
}

const STATUS_ORDER = ['declared', 'observed-in-source', 'inferred', 'measured', 'unknown']
const KIND_ORDER = ['structural', 'behavioural']

function countBy(values: string[], order: string[]): Array<[string, number]> {
  const counts = new Map<string, number>()
  for (const value of values) counts.set(value, (counts.get(value) ?? 0) + 1)

  return [...counts.entries()].sort((a, b) => {
    const ai = order.indexOf(a[0])
    const bi = order.indexOf(b[0])
    return (ai === -1 ? order.length : ai) - (bi === -1 ? order.length : bi)
  })
}

/**
 * Overlay summary of the truth labels on the visible model. Counts only what is on screen
 * so the overlay matches the canvas it describes.
 */
export function TruthLegend({ factStatuses, relationshipKinds, onClose }: TruthLegendProps) {
  const statuses = countBy(factStatuses, STATUS_ORDER)
  const kinds = countBy(relationshipKinds, KIND_ORDER)

  return (
    <Panel position="top-left" className="rounded-lg border border-border bg-card/95 px-3 py-2 shadow-md backdrop-blur">
      <div className="flex items-center justify-between gap-4">
        <span className="text-xs font-medium text-foreground">Truth labels</span>
        <button
          type="button"
          aria-label="Close truth labels"
          className="text-muted-foreground hover:text-foreground"
          onClick={onClose}
        >
          ×
        </button>
      </div>

      <ul className="mt-1.5 space-y-0.5">
        {statuses.map(([status, count]) => (
          <li key={status} className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
            <span className="h-2 w-2 rounded-full" style={{ backgroundColor: factStatusColor(status) }} />
            <span>{factStatusLabel(status)}</span>
            <span className="font-mono text-foreground">{count}</span>
          </li>
        ))}
      </ul>

      <div className="my-1.5 border-t border-border" />

      <ul className="space-y-0.5">
        {kinds.map(([kind, count]) => (
          <li key={kind} className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
            <span
              className="h-0.5 w-3 rounded-full"
              style={{ backgroundColor: kind === 'behavioural' ? '#2dd4bf' : 'var(--muted-foreground)' }}
            />
            <span>{kind === 'behavioural' ? 'Behavioural' : 'Structural'}</span>
            <span className="font-mono text-foreground">{count}</span>
          </li>
        ))}
      </ul>
    </Panel>
  )
}
