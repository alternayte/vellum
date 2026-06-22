import type { MergeChange } from '@/stores/draft-store'

interface DiffDetailProps {
  change: MergeChange
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function DiffRow({ field, baseValue, newValue }: { field: string; baseValue: unknown; newValue: unknown }) {
  const base = formatValue(baseValue)
  const next = formatValue(newValue)
  const changed = base !== next

  return (
    <tr className="border-t border-border">
      <td className="py-1.5 pr-3 align-top text-xs text-muted-foreground">{field}</td>
      <td className={`py-1.5 pr-3 align-top text-xs ${changed ? 'text-red-400 line-through' : 'text-foreground'}`}>
        {base}
      </td>
      <td className={`py-1.5 align-top text-xs ${changed ? 'text-emerald-400' : 'text-foreground'}`}>
        {next}
      </td>
    </tr>
  )
}

export function DiffDetail({ change }: DiffDetailProps) {
  const resolved = change.resolvedValue as Record<string, unknown> | null

  if (!resolved) {
    return (
      <div className="mt-4">
        <p className="text-xs text-muted-foreground">
          {change.changeKind === 'added' ? 'Added' : 'Removed'} — no field-level diff available.
        </p>
      </div>
    )
  }

  const fields = Object.keys(resolved).filter((k) => k !== 'id')

  return (
    <div className="mt-4">
      <p className="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
        {change.changeKind === 'added' ? 'Added' : change.changeKind === 'removed' ? 'Removed' : 'Modified'}
      </p>
      <table className="w-full text-left">
        <thead>
          <tr>
            <th className="pb-1 text-[10px] uppercase tracking-wider text-muted-foreground">Field</th>
            <th className="pb-1 text-[10px] uppercase tracking-wider text-muted-foreground">Before</th>
            <th className="pb-1 text-[10px] uppercase tracking-wider text-muted-foreground">After</th>
          </tr>
        </thead>
        <tbody>
          {fields.map((field) => (
            <DiffRow
              key={field}
              field={field}
              baseValue={null}
              newValue={(resolved as Record<string, unknown>)[field]}
            />
          ))}
        </tbody>
      </table>
    </div>
  )
}
