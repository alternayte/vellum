import { useId, type ReactNode } from 'react'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { fieldMessages } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { SOURCE_PATTERNS, type PromiseInput } from '@/lib/promises-api'
import { TRACE_EVENT_KINDS } from '@/lib/trace'
import type { ScenarioReference } from '@/components/trace/scenario-editor'

export const labelClass = 'block text-xs font-medium text-muted-foreground'
export const areaClass =
  'mt-1 min-h-16 w-full resize-y rounded-lg border border-input bg-transparent p-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-70 aria-invalid:border-destructive'
/** The select of the settings screens, at the height of the small controls of a dialog. */
export const smallSelect =
  'h-7 w-full min-w-0 rounded-lg border border-input bg-transparent px-2 text-xs text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-50 aria-invalid:border-destructive'

/**
 * One labelled control with the server's refusal of its field next to it. `children`
 * receives the ID of the control and what it must name in aria-describedby.
 */
export function Field({ label, hint, error, field, className, children }: {
  label: string
  hint?: string
  /** The error of the last request. Its messages for `field` are shown here. */
  error: unknown
  field: string
  className?: string
  children: (control: { id: string; 'aria-describedby': string | undefined; 'aria-invalid': true | undefined }) => ReactNode
}) {
  const id = useId()
  const messages = fieldMessages(error, field)
  const describedBy = [hint ? `${id}-hint` : null, messages.length > 0 ? `${id}-error` : null].filter(Boolean).join(' ') || undefined
  return (
    <div className={cn('min-w-0', className)}>
      <label htmlFor={id} className={labelClass}>{label}</label>
      {children({ id, 'aria-describedby': describedBy, 'aria-invalid': messages.length > 0 ? true : undefined })}
      {hint && <p id={`${id}-hint`} className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{hint}</p>}
      {messages.length > 0 && <p id={`${id}-error`} role="alert" className="mt-1 text-xs text-destructive">{messages.join(' ')}</p>}
    </div>
  )
}

/** The error of a request, with each refused field that no control on the screen shows. */
export function Failure({ error, shown = [] }: { error: Error; shown?: string[] }) {
  const rest = error instanceof ApiError
    ? error.fields.filter((e) => !shown.some((field) => e.field === field || e.field.startsWith(`${field}.`) || e.field.startsWith(`${field}[`)))
    : []
  return (
    <div role="alert" className="text-xs text-destructive">
      <p>{error.message}</p>
      {rest.length > 0 && (
        <ul className="mt-0.5 list-disc pl-4">
          {rest.map((e, i) => <li key={i}>{e.field}: {e.message}</li>)}
        </ul>
      )}
    </div>
  )
}

const GROUPS: ScenarioReference['group'][] = ['Elements', 'Operations', 'Channels', 'Subscriptions', 'Messages']

/**
 * A part of the model, chosen by name. The value is its ID; nobody types one. An ID that
 * the model no longer holds stays selectable, so the form shows what is stored.
 */
export function ReferenceSelect({ value, onChange, references, groups = GROUPS, empty, className, ...control }: {
  value: string | null
  onChange: (id: string | null) => void
  references: ScenarioReference[]
  groups?: ScenarioReference['group'][]
  /** The label of "no part". Without it a part must be chosen. */
  empty?: string
  className?: string
  id?: string
  disabled?: boolean
  'aria-label'?: string
  'aria-describedby'?: string
  'aria-invalid'?: true
}) {
  const known = value === null || references.some((r) => r.id === value && groups.includes(r.group))
  return (
    <select {...control} className={cn(smallSelect, className)} value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
      {(empty !== undefined || value === null) && <option value="">{empty ?? 'Choose…'}</option>}
      {!known && <option value={value!}>No longer in the model ({value!.slice(0, 8)})</option>}
      {groups.map((group) => {
        const items = references.filter((r) => r.group === group)
        if (items.length === 0) return null
        return (
          <optgroup key={group} label={group}>
            {items.map((item) => <option key={item.id} value={item.id}>{item.label}</option>)}
          </optgroup>
        )
      })}
    </select>
  )
}

/**
 * The structured part of a forbidden outcome: trace events that must not occur. A scenario
 * check counts each one, for the target when one is chosen, and the count must be zero.
 */
export function ForbiddenEvents({ value, onChange, references, disabled, error }: {
  value: PromiseInput['forbiddenEvents']
  onChange: (value: PromiseInput['forbiddenEvents']) => void
  references: ScenarioReference[]
  disabled: boolean
  error: unknown
}) {
  const messages = fieldMessages(error, 'forbiddenEvents')
  return (
    <fieldset className="min-w-0 md:col-span-2" aria-describedby="forbidden-events-help">
      <legend className={labelClass}>Forbidden events</legend>
      <p id="forbidden-events-help" className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
        An event that must not occur in a played scenario. A generated scenario check asserts that it occurs zero times. The text of the forbidden outcome alone is not checked.
      </p>
      {value.length > 0 && (
        <ul className="mt-1.5 space-y-1.5">
          {value.map((event, i) => (
            <li key={i} className="grid grid-cols-[1fr_auto] gap-1.5 sm:grid-cols-[minmax(0,12rem)_minmax(0,1fr)_auto]">
              <select
                className={smallSelect} aria-label={`Forbidden event ${i + 1}: event`} value={event.kind} disabled={disabled}
                onChange={(e) => onChange(value.map((x, j) => (j === i ? { ...x, kind: e.target.value } : x)))}
              >
                {TRACE_EVENT_KINDS.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
              </select>
              <ReferenceSelect
                className="max-sm:col-span-2 max-sm:row-start-2" aria-label={`Forbidden event ${i + 1}: where`} empty="Anywhere in the scenario"
                value={event.target} references={references} disabled={disabled}
                onChange={(target) => onChange(value.map((x, j) => (j === i ? { ...x, target } : x)))}
              />
              {!disabled && (
                <Button type="button" variant="ghost" size="sm" className="h-7 w-7 p-0 max-sm:col-start-2 max-sm:row-start-1" aria-label={`Remove forbidden event ${i + 1}`} onClick={() => onChange(value.filter((_, j) => j !== i))}>
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
      {messages.length > 0 && <p role="alert" className="mt-1 text-xs text-destructive">{messages.join(' ')}</p>}
      {!disabled && (
        <Button type="button" variant="secondary" size="sm" className="mt-1.5 h-7 text-xs" onClick={() => onChange([...value, { kind: 'Dropped', target: null }])}>
          Add a forbidden event
        </Button>
      )}
      {disabled && value.length === 0 && <p className="mt-1 text-xs text-muted-foreground">None.</p>}
    </fieldset>
  )
}

/**
 * A pattern that the source of a repository must declare, for one service when chosen.
 * `prefix` is the name of the value in the request, so the server's refusal of a part of
 * it shows next to that part.
 */
export function PatternFields({ value, onChange, repositories, references, disabled, error, prefix }: {
  value: { repository: string; pattern: string; service: string | null }
  onChange: (value: { repository: string; pattern: string; service: string | null }) => void
  /** `owner/name` of each repository the project has a completed scan of. */
  repositories: string[]
  references: ScenarioReference[]
  disabled: boolean
  error: unknown
  prefix: string
}) {
  const listed = repositories.includes(value.repository) || value.repository === '' ? repositories : [value.repository, ...repositories]
  return (
    <div className="grid gap-2 sm:grid-cols-3">
      <Field
        label="Repository" field={`${prefix}.repository`} error={error}
        hint={repositories.length === 0 ? 'This project has no completed scan. Import the repository first: a check reads a scan.' : 'A check reads one completed scan of this repository.'}
      >
        {(control) => repositories.length === 0 ? (
          <Input {...control} className="mt-1 h-7 text-xs" value={value.repository} placeholder="owner/name" spellCheck={false} disabled={disabled} onChange={(e) => onChange({ ...value, repository: e.target.value })} />
        ) : (
          <select {...control} className={cn(smallSelect, 'mt-1')} value={value.repository} disabled={disabled} onChange={(e) => onChange({ ...value, repository: e.target.value })}>
            {value.repository === '' && <option value="">Choose…</option>}
            {listed.map((repository) => <option key={repository} value={repository}>{repository}</option>)}
          </select>
        )}
      </Field>
      <Field label="Pattern" field={`${prefix}.pattern`} error={error} hint="What the import reads from the source and labels with this name.">
        {(control) => (
          <select {...control} className={cn(smallSelect, 'mt-1')} value={value.pattern} disabled={disabled} onChange={(e) => onChange({ ...value, pattern: e.target.value })}>
            {!SOURCE_PATTERNS.some((p) => p === value.pattern) && <option value={value.pattern}>{value.pattern || 'Choose…'}</option>}
            {SOURCE_PATTERNS.map((pattern) => <option key={pattern} value={pattern}>{pattern}</option>)}
          </select>
        )}
      </Field>
      <Field label="Service" field={`${prefix}.service`} error={error} hint="An element that an accepted import links to a service of the repository.">
        {(control) => (
          <ReferenceSelect
            {...control} className="mt-1" empty="The whole repository" groups={['Elements']}
            value={value.service} references={references} disabled={disabled} onChange={(service) => onChange({ ...value, service })}
          />
        )}
      </Field>
    </div>
  )
}
