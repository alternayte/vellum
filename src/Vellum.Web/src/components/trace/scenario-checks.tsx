import { useId } from 'react'
import { Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import {
  EVENT_KINDS, FAULT_KINDS, OUTCOMES, nextAssertionId,
  type Assertion, type EventKind, type Fault, type FaultKind, type Outcome,
} from '@/lib/scenario-document'
import { selectClass } from './contract-parts'
import { ReferencePicker } from './reference-picker'
import { useCatalog } from './scenario-catalog'

const FAULT_HELP: Record<FaultKind, string> = {
  Error: 'The attempt fails at once.',
  Timeout: 'The attempt takes longer than the operation’s timeout.',
  Outage: 'The target is down: the attempt fails at once.',
}

/** The failures to inject. One target has at most one fault. */
export function FaultsEditor({ faults, onChange }: { faults: Fault[]; onChange: (faults: Fault[]) => void }) {
  const catalog = useCatalog()
  const set = (index: number, fields: Partial<Fault>) => onChange(faults.map((f, i) => (i === index ? { ...f, ...fields } : f)))
  const used = new Set(faults.map((f) => f.target))

  return (
    <div>
      {faults.length === 0 && <p className="text-xs leading-relaxed text-muted-foreground">No failure is injected. Add one to see retries, timeouts, failure handling and dead letters.</p>}
      <ul className="space-y-2">
        {faults.map((fault, index) => {
          const operation = catalog.operation(fault.target)
          const canTimeOut = !!operation && operation.timeoutMs > 0
          return (
            <li key={index}>
              <FaultRow
                fault={fault} index={index} canTimeOut={canTimeOut}
                targets={catalog.faultTargets.filter((t) => t.id === fault.target || !used.has(t.id))}
                onChange={(fields) => set(index, fields)}
                onRemove={() => onChange(faults.filter((_, i) => i !== index))}
              />
            </li>
          )
        })}
      </ul>
      <Button type="button" variant="ghost" size="sm" className="mt-1.5 h-6 gap-1 px-1.5 text-xs" onClick={() => onChange([...faults, { target: '', kind: 'Error', failFirst: 1 }])}>
        <Plus className="h-3 w-3" /> Add a failure
      </Button>
    </div>
  )
}

function FaultRow({ fault, index, canTimeOut, targets, onChange, onRemove }: {
  fault: Fault
  index: number
  canTimeOut: boolean
  targets: ReturnType<typeof useCatalog>['faultTargets']
  onChange: (fields: Partial<Fault>) => void
  onRemove: () => void
}) {
  const kindId = useId()
  const countId = useId()
  const timeoutWrong = fault.kind === 'Timeout' && fault.target && !canTimeOut
  return (
    <div role="group" aria-label={`Failure ${index + 1}`} className="rounded-lg border border-border bg-background/40 p-2.5">
      <div className="grid items-end gap-2 sm:grid-cols-[minmax(0,1fr)_8rem_11rem_auto]">
        <ReferencePicker label="Target" value={fault.target || null} options={targets} onChange={(id) => onChange({ target: id ?? '' })} placeholder="Choose what fails" />
        <div>
          <label htmlFor={kindId} className="block text-[11px] font-medium text-muted-foreground">Kind</label>
          <select id={kindId} className={cn(selectClass, 'mt-1')} value={fault.kind} onChange={(e) => onChange({ kind: e.target.value as FaultKind })} aria-invalid={timeoutWrong ? true : undefined}>
            {FAULT_KINDS.map((k) => <option key={k} value={k}>{k}</option>)}
          </select>
        </div>
        <div>
          <label htmlFor={countId} className="block text-[11px] font-medium text-muted-foreground">Fails</label>
          <div className="mt-1 flex items-center gap-1.5">
            <select
              className={selectClass} value={fault.failFirst === null ? 'every' : 'first'} aria-label="Which attempts fail"
              onChange={(e) => onChange({ failFirst: e.target.value === 'every' ? null : 1 })}
            >
              <option value="first">the first</option>
              <option value="every">every attempt</option>
            </select>
            {fault.failFirst !== null && (
              <Input
                id={countId} type="number" min={1} max={10} className="h-8 w-14 text-sm" value={fault.failFirst} aria-label="Number of attempts that fail"
                onChange={(e) => onChange({ failFirst: Math.max(1, Number(e.target.value) || 1) })}
              />
            )}
          </div>
        </div>
        <Button type="button" variant="ghost" size="icon" className="h-8 w-8 hover:text-[var(--trace-failure)]" onClick={onRemove} aria-label={`Remove failure ${index + 1}`}>
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </div>
      <p className={cn('mt-1.5 text-[11px]', timeoutWrong ? 'text-[var(--trace-failure)]' : 'text-muted-foreground')} role={timeoutWrong ? 'alert' : undefined}>
        {timeoutWrong ? 'Only an operation with a timeout can time out. Choose Error or Outage, or give the operation a timeout.' : FAULT_HELP[fault.kind]}
        {fault.failFirst !== null && !timeoutWrong && ` Attempts after the first ${fault.failFirst} succeed.`}
      </p>
    </div>
  )
}

const EVENT_TEXT: Record<EventKind, string> = {
  RequestSent: 'requests sent', ResponseReceived: 'responses received', RequestFailed: 'failed requests', RequestTimedOut: 'timed-out requests',
  RetryScheduled: 'retries', Published: 'publishes', PublishFailed: 'failed publishes', Delivered: 'deliveries', DeliveryFailed: 'failed deliveries',
  DeadLettered: 'dead letters', Dropped: 'dropped messages', DataWritten: 'writes', DataWriteFailed: 'failed writes', BranchTaken: 'branches taken',
  StepSkipped: 'skipped steps', Routed: 'routings', NotRouted: 'routes not taken',
}

/** The expected results: the outcome of a step, or a count of trace events. */
export function AssertionsEditor({ assertions, stepIds, stepSummary, results, onChange }: {
  assertions: Assertion[]
  stepIds: string[]
  stepSummary: (id: string) => string | undefined
  /** The server's problems with the expected results, by assertion ID. */
  results: Record<string, string[]>
  onChange: (assertions: Assertion[]) => void
}) {
  const catalog = useCatalog()
  const ids = assertions.map((a) => a.id)
  const set = (index: number, next: Assertion) => onChange(assertions.map((a, i) => (i === index ? next : a)))
  const stepOptions = stepIds.map((id) => ({ id, label: id, detail: stepSummary(id) }))

  return (
    <div>
      {assertions.length === 0 && <p className="text-xs leading-relaxed text-muted-foreground">No result is expected yet. A played scenario passes or fails each expected result.</p>}
      <ul className="space-y-2">
        {assertions.map((assertion, index) => (
          <li key={index} className="rounded-lg border border-border bg-background/40 p-2.5">
            <div className="flex items-end gap-2">
              {assertion.type === 'outcome' ? (
                <div className="grid min-w-0 flex-1 items-end gap-2 sm:grid-cols-[minmax(0,1fr)_9rem]">
                  <ReferencePicker label="Step" value={assertion.step || null} options={stepOptions} onChange={(id) => set(index, { ...assertion, step: id ?? '' })} placeholder="Choose a step" emptyText="Add a step first" />
                  <OutcomeSelect value={assertion.expected} onChange={(expected) => set(index, { ...assertion, expected })} />
                </div>
              ) : (
                <div className="grid min-w-0 flex-1 items-end gap-2 sm:grid-cols-[5rem_12rem_minmax(0,1fr)]">
                  <CountInput value={assertion.expected} onChange={(expected) => set(index, { ...assertion, expected })} />
                  <EventKindSelect value={assertion.kind} onChange={(kind) => set(index, { ...assertion, kind })} />
                  <ReferencePicker
                    label="For" value={assertion.target} options={catalog.eventTargets} allowNone noneLabel="Any part"
                    onChange={(target) => set(index, { ...assertion, target })} placeholder="Any part"
                  />
                </div>
              )}
              <Button type="button" variant="ghost" size="icon" className="h-8 w-8 hover:text-[var(--trace-failure)]" onClick={() => onChange(assertions.filter((_, i) => i !== index))} aria-label={`Remove expected result ${assertion.id}`}>
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </div>
            <p className="mt-1.5 text-[11px] text-muted-foreground">
              {assertion.type === 'outcome'
                ? 'Read from the attempt that settled the call or delivery. A step with no outcome counts as Skipped.'
                : `Counts the ${EVENT_TEXT[assertion.kind]} in the trace${assertion.target ? ' that involve this part' : ''}, retried attempts too.`}
              <span className="ml-1 font-mono">{assertion.id}</span>
            </p>
            {(results[assertion.id] ?? []).map((problem, i) => <p key={i} role="alert" className="mt-1 text-[11px] text-[var(--trace-failure)]">{problem}</p>)}
          </li>
        ))}
      </ul>
      <div className="mt-1.5 flex flex-wrap gap-1">
        <Button type="button" variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => onChange([...assertions, { type: 'outcome', id: nextAssertionId(ids), step: '', expected: 'Succeeded' }])}>
          <Plus className="h-3 w-3" /> Step outcome
        </Button>
        <Button type="button" variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => onChange([...assertions, { type: 'event-count', id: nextAssertionId(ids), kind: 'DeadLettered', target: null, expected: 0 }])}>
          <Plus className="h-3 w-3" /> Event count
        </Button>
      </div>
    </div>
  )
}

function OutcomeSelect({ value, onChange }: { value: Outcome; onChange: (value: Outcome) => void }) {
  const id = useId()
  return (
    <div>
      <label htmlFor={id} className="block text-[11px] font-medium text-muted-foreground">Ends</label>
      <select id={id} className={cn(selectClass, 'mt-1')} value={value} onChange={(e) => onChange(e.target.value as Outcome)}>
        {OUTCOMES.map((o) => <option key={o} value={o}>{o}</option>)}
      </select>
    </div>
  )
}

function CountInput({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const id = useId()
  return (
    <div>
      <label htmlFor={id} className="block text-[11px] font-medium text-muted-foreground">Exactly</label>
      <Input id={id} type="number" min={0} className="mt-1 h-8 text-sm" value={value} onChange={(e) => onChange(Math.max(0, Math.floor(Number(e.target.value) || 0)))} />
    </div>
  )
}

function EventKindSelect({ value, onChange }: { value: EventKind; onChange: (value: EventKind) => void }) {
  const id = useId()
  return (
    <div>
      <label htmlFor={id} className="block text-[11px] font-medium text-muted-foreground">Events</label>
      <select id={id} className={cn(selectClass, 'mt-1')} value={value} onChange={(e) => onChange(e.target.value as EventKind)}>
        {EVENT_KINDS.map((k) => <option key={k} value={k}>{EVENT_TEXT[k]} ({k})</option>)}
      </select>
    </div>
  )
}
