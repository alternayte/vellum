import type { FieldChange } from '@/lib/model-diff'

const CHIP: Record<string, { label: string; color: string }> = {
  added: { label: 'Added', color: 'var(--trace-request)' },
  // A preview of an import says what the import would do, not what was done.
  add: { label: 'Add', color: 'var(--trace-request)' },
  change: { label: 'Change', color: 'var(--trace-event)' },
  modified: { label: 'Modified', color: 'var(--trace-event)' },
  changed: { label: 'Changed', color: 'var(--trace-event)' },
  removed: { label: 'Removed', color: 'var(--trace-failure)' },
  refused: { label: 'Refused', color: 'var(--trace-failure)' },
  unchanged: { label: 'Unchanged', color: 'var(--muted-foreground)' },
}

/** The kind of a change as a word in a coloured frame. The word carries the meaning; the colour repeats it. */
export function ChangeChip({ kind }: { kind: string }) {
  const chip = CHIP[kind] ?? { label: kind, color: 'var(--muted-foreground)' }
  return (
    <span className="shrink-0 rounded-full border px-1.5 text-[11px] font-medium" style={{ color: chip.color, borderColor: chip.color }}>
      {chip.label}
    </span>
  )
}

/** The fields of one fact that differ between two models, one row for each field. */
export function FieldTable({ left, right, fields, empty }: { left: string; right: string; fields: FieldChange[]; empty: string }) {
  if (fields.length === 0) return <p className="mt-1.5 text-xs text-muted-foreground">{empty}</p>
  return (
    <div className="mt-1.5 overflow-x-auto">
      <table className="w-full text-left text-xs">
        <thead className="text-[10px] uppercase tracking-wider text-muted-foreground">
          <tr><th className="py-1 pr-3 font-medium">Field</th><th className="py-1 pr-3 font-medium">{left}</th><th className="py-1 font-medium">{right}</th></tr>
        </thead>
        <tbody>
          {fields.map((field) => (
            <tr key={field.field} className="border-t border-border align-top">
              <td className="py-1 pr-3 text-muted-foreground">{field.field}</td>
              <td className="break-words py-1 pr-3 text-foreground">{field.left}</td>
              <td className="break-words py-1 text-foreground">{field.right}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
