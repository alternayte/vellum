import { useId, type ReactNode } from 'react'
import { Pencil, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'

export interface NamedElement {
  id: string
  name: string
}

/** The choices of a schema select: none, then each schema with its version. */
export function SchemaOptions({ schemas }: { schemas: { id: string; name: string; version: number }[] }) {
  return (
    <>
      <option value="">None</option>
      {schemas.map((schema) => <option key={schema.id} value={schema.id}>{schema.name} · v{schema.version}</option>)}
    </>
  )
}

export const selectClass =
  'h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive'

/**
 * A labelled control with the server's reason under it. The child is a function of the
 * props that tie the control to that reason.
 */
export function Field({
  label, error, children,
}: {
  label: string
  error?: string
  children: ReactNode | ((invalid: { 'aria-invalid': true | undefined; 'aria-describedby': string | undefined }) => ReactNode)
}) {
  const errorId = useId()
  const invalid = { 'aria-invalid': error ? true as const : undefined, 'aria-describedby': error ? errorId : undefined }
  return (
    <div>
      <label className="block text-[11px] font-medium text-muted-foreground">
        {label}
        <span className="mt-1 block">{typeof children === 'function' ? children(invalid) : children}</span>
      </label>
      {error && <p id={errorId} role="alert" className="mt-1 text-[11px] leading-snug text-[var(--trace-failure)]">{error}</p>}
    </div>
  )
}

export function FormError({ message }: { message: string | null }) {
  if (!message) return null
  return <p role="alert" className="text-[11px] leading-snug text-[var(--trace-failure)]">{message}</p>
}

export function Section({
  title, count, hint, adding, onToggle, children,
}: {
  title: string
  count: number
  hint: string
  /** True while the section shows its form. Leave it out when Add opens a dialog. */
  adding?: boolean
  onToggle: () => void
  children: ReactNode
}) {
  return (
    <section className="border-b border-border px-3 py-3 last:border-b-0">
      <div className="flex items-center justify-between">
        <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          {title} <span className="font-mono text-muted-foreground/70">{count}</span>
        </h3>
        <Button
          variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={onToggle} aria-expanded={adding}
          aria-label={adding ? `Cancel adding to ${title}` : `Add to ${title}`}
        >
          <Plus className="h-3 w-3" />
          {adding ? 'Cancel' : 'Add'}
        </Button>
      </div>
      {count === 0 && !adding && <p className="mt-1.5 text-xs leading-relaxed text-muted-foreground">{hint}</p>}
      {children}
    </section>
  )
}

const rowButton =
  'mt-0.5 rounded p-1 text-muted-foreground opacity-0 focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-primary group-hover:opacity-100 pointer-coarse:opacity-100'

/** One contract in a list. `error` is the server's reason for a refused removal of this row. */
export function Row({
  title, detail, onEdit, editLabel, onRemove, removeLabel, error,
}: {
  title: string
  detail: string
  onEdit?: () => void
  editLabel?: string
  onRemove: () => void
  removeLabel: string
  error?: string | null
}) {
  return (
    <li className="group rounded-md px-1.5 py-1 hover:bg-muted">
      <div className="flex items-start gap-1">
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm text-foreground">{title}</p>
          <p className="break-words font-mono text-[10px] leading-snug text-muted-foreground">{detail}</p>
        </div>
        {onEdit && (
          <button type="button" onClick={onEdit} aria-label={editLabel} className={`${rowButton} hover:text-foreground`}>
            <Pencil className="h-3.5 w-3.5" />
          </button>
        )}
        <button type="button" onClick={onRemove} aria-label={removeLabel} className={`${rowButton} hover:text-[var(--trace-failure)]`}>
          <Trash2 className="h-3.5 w-3.5" />
        </button>
      </div>
      {error && <p role="alert" className="mt-1 break-words text-[11px] leading-snug text-[var(--trace-failure)]">{error}</p>}
    </li>
  )
}
