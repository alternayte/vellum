import { useId, type ReactNode } from 'react'
import { RotateCcw } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

interface RowProps {
  label: string
  hint?: string
  /** True when the workspace overrides the instance default. */
  overridden?: boolean
  onReset?: () => void
  error?: string
  htmlFor?: string
  children: ReactNode
}

/** The IDs of the hint and of the error of the control `id`. The control names them in aria-describedby. */
function describedBy(id: string, row: { hint?: string; error?: string }): string | undefined {
  return [row.hint ? `${id}-hint` : null, row.error ? `${id}-error` : null].filter(Boolean).join(' ') || undefined
}

/** One setting: its label, its control, where its value comes from, and any refusal. */
export function SettingRow({ label, hint, overridden, onReset, error, htmlFor, children }: RowProps) {
  return (
    <div className="grid gap-x-6 gap-y-1.5 border-b border-border py-4 last:border-b-0 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]">
      <div>
        <label htmlFor={htmlFor} className="text-sm font-medium text-foreground">{label}</label>
        {hint && <p id={htmlFor ? `${htmlFor}-hint` : undefined} className="mt-0.5 text-xs leading-relaxed text-muted-foreground">{hint}</p>}
      </div>
      <div className="min-w-0">
        {children}
        <div className="mt-1.5 flex min-h-4 items-center gap-2 text-[11px]">
          {error ? (
            <span id={htmlFor ? `${htmlFor}-error` : undefined} role="alert" className="text-destructive">{error}</span>
          ) : overridden === undefined ? null : overridden ? (
            <>
              <span className="rounded-full border border-primary/50 px-1.5 text-primary">Set for this workspace</span>
              {onReset && (
                <button
                  type="button"
                  onClick={onReset}
                  className="flex items-center gap-1 rounded text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
                >
                  <RotateCcw className="h-3 w-3" aria-hidden="true" />
                  Use the default
                </button>
              )}
            </>
          ) : (
            <span className="text-muted-foreground">Instance default</span>
          )}
        </div>
      </div>
    </div>
  )
}

interface FieldProps<T> extends Omit<RowProps, 'children' | 'htmlFor'> {
  value: T
  onChange: (value: T) => void
}

export function TextField({ value, onChange, placeholder, type = 'text', ...row }: FieldProps<string> & { placeholder?: string; type?: string }) {
  const id = useId()
  return (
    <SettingRow {...row} htmlFor={id}>
      <Input id={id} type={type} value={value} placeholder={placeholder} aria-invalid={row.error ? true : undefined} aria-describedby={describedBy(id, row)} onChange={(e) => onChange(e.target.value)} />
    </SettingRow>
  )
}

/** An empty box means "no value" when `nullable` is set. */
export function NumberField<T extends number | null>({
  value, onChange, min, max, step, nullable, suffix, ...row
}: FieldProps<T> & { min?: number; max?: number; step?: number; nullable?: boolean; suffix?: string }) {
  const id = useId()
  return (
    <SettingRow {...row} htmlFor={id}>
      <div className="flex items-center gap-2">
        <Input
          id={id}
          type="number"
          className="max-w-44"
          value={value ?? ''}
          min={min}
          max={max}
          step={step}
          placeholder={nullable ? 'No limit' : undefined}
          aria-invalid={row.error ? true : undefined}
          aria-describedby={describedBy(id, row)}
          onChange={(e) => {
            if (e.target.value === '') {
              if (nullable) onChange(null as T)
              return
            }
            const parsed = Number(e.target.value)
            if (Number.isFinite(parsed)) onChange(parsed as T)
          }}
        />
        {suffix && <span className="text-xs text-muted-foreground">{suffix}</span>}
      </div>
    </SettingRow>
  )
}

export function ToggleField({ value, onChange, disabled, ...row }: FieldProps<boolean> & { disabled?: boolean }) {
  const id = useId()
  return (
    <SettingRow {...row} htmlFor={id}>
      <Toggle id={id} checked={value} onChange={onChange} disabled={disabled} describedBy={describedBy(id, row)} />
    </SettingRow>
  )
}

export function Toggle({ id, checked, onChange, disabled, label, describedBy: description }: { id?: string; checked: boolean; onChange: (checked: boolean) => void; disabled?: boolean; label?: string; describedBy?: string }) {
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      aria-describedby={description}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={cn(
        'relative h-5 w-9 rounded-full border transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary disabled:cursor-not-allowed disabled:opacity-50',
        checked ? 'border-primary bg-primary' : 'border-border bg-muted',
      )}
    >
      <span className={cn('absolute top-0.5 h-3.5 w-3.5 rounded-full transition-[left]', checked ? 'left-[1.125rem] bg-primary-foreground' : 'left-0.5 bg-foreground')} />
    </button>
  )
}

export const selectClass =
  'h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-50'

export function SelectField({
  value, onChange, options, ...row
}: FieldProps<string> & { options: { value: string; label: string }[] }) {
  const id = useId()
  return (
    <SettingRow {...row} htmlFor={id}>
      <select id={id} className={cn(selectClass, 'max-w-80')} value={value} aria-invalid={row.error ? true : undefined} aria-describedby={describedBy(id, row)} onChange={(e) => onChange(e.target.value)}>
        {options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
      </select>
    </SettingRow>
  )
}

export function SectionHeading({ title, description }: { title: string; description: string }) {
  return (
    <header className="pb-1">
      <h2 className="font-heading text-lg font-medium text-foreground">{title}</h2>
      <p className="mt-1 max-w-prose text-sm text-muted-foreground">{description}</p>
    </header>
  )
}
