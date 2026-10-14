import { useContext, useEffect, useId, useState, type ReactNode } from 'react'
import { ArrowDown, ArrowUp, GitBranch, Plus, Send, Split, Trash2, Database, PhoneOutgoing, X, MessageSquare } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import {
  OUTCOMES, finalBefore, move, newStep, nextStepId,
  type BranchStep, type CallStep, type Outcome, type ParallelStep, type PublishStep, type Step, type StepType, type WriteStep,
} from '@/lib/scenario-document'
import { selectClass } from './contract-parts'
import { ReferencePicker } from './reference-picker'
import { StepEditingContext, describeStep, useCatalog } from './scenario-catalog'
import { TargetThreads } from '@/components/comments/target-threads'
import { useOpenThreadCounts } from '@/components/comments/use-open-thread-counts'

const KIND: Record<StepType, { label: string; color: string; icon: typeof Send }> = {
  call: { label: 'Call', color: 'var(--trace-request)', icon: PhoneOutgoing },
  publish: { label: 'Publish', color: 'var(--trace-event)', icon: Send },
  write: { label: 'Write', color: 'var(--trace-data)', icon: Database },
  parallel: { label: 'Parallel', color: 'var(--trace-control)', icon: Split },
  branch: { label: 'Branch', color: 'var(--trace-control)', icon: GitBranch },
}

const ADD_HINT: Record<StepType, string> = {
  call: 'A synchronous request to an operation',
  publish: 'A message to a channel',
  write: 'A change to a data store',
  parallel: 'Branches that start at the same time',
  branch: 'A path chosen by the outcome of an earlier step',
}

/**
 * An ordered list of steps. `inherited` holds the steps whose outcome is final before this
 * list starts; a branch may read those and the steps before it in the list.
 */
export function StepList({
  steps, onChange, inherited, title, accent, empty, first,
}: {
  steps: Step[]
  onChange: (steps: Step[]) => void
  inherited: string[]
  title?: string
  /** The colour of the rule beside a nested list. */
  accent?: string
  empty?: string
  /** Names the first step of the top list as the trigger. */
  first?: string
}) {
  const { allIds } = useContext(StepEditingContext)
  const before = finalBefore(steps, inherited)
  const add = (type: StepType) => onChange([...steps, newStep(type, nextStepId(type, allIds))])
  const titleId = useId()

  return (
    <section aria-labelledby={title ? titleId : undefined} className={cn(title && 'mt-2 border-l-2 pl-3')} style={accent ? { borderColor: accent } : title ? { borderColor: 'var(--border)' } : undefined}>
      {title && <h4 id={titleId} className="mb-1.5 text-[11px] font-medium text-muted-foreground">{title}</h4>}
      {steps.length === 0 && empty && <p className="mb-1.5 text-xs leading-relaxed text-muted-foreground">{empty}</p>}
      <ol className="space-y-2">
        {steps.map((step, index) => (
          <li key={`${index}-${step.id}`}>
            <StepCard
              step={step}
              position={index + 1}
              count={steps.length}
              badge={first && index === 0 ? first : undefined}
              available={before[index]}
              onChange={(next) => onChange(steps.map((s, i) => (i === index ? next : s)))}
              onMove={(by) => onChange(move(steps, index, by))}
              onRemove={() => onChange(steps.filter((_, i) => i !== index))}
            />
          </li>
        ))}
      </ol>
      <div className="mt-1.5 flex flex-wrap items-center gap-1" role="group" aria-label={title ? `Add a step to ${title}` : 'Add a step'}>
        <span className="mr-0.5 text-[11px] text-muted-foreground">Add</span>
        {(Object.keys(KIND) as StepType[]).map((type) => {
          const Icon = KIND[type].icon
          return (
            <Button key={type} type="button" variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" title={ADD_HINT[type]} onClick={() => add(type)}>
              <Icon aria-hidden="true" className="h-3 w-3" style={{ color: KIND[type].color }} />
              {KIND[type].label}
            </Button>
          )
        })}
      </div>
    </section>
  )
}

function StepCard({
  step, position, count, badge, available, onChange, onMove, onRemove,
}: {
  step: Step
  position: number
  count: number
  badge?: string
  available: string[]
  onChange: (step: Step) => void
  onMove: (by: -1 | 1) => void
  onRemove: () => void
}) {
  const catalog = useCatalog()
  const { issues, comments } = useContext(StepEditingContext)
  const [showComments, setShowComments] = useState(false)
  const kind = KIND[step.type]
  const Icon = kind.icon
  const problems = issues[step.id] ?? []
  const headingId = useId()

  return (
    <article
      aria-labelledby={headingId}
      className={cn('rounded-lg border bg-background/40 p-2.5', problems.length > 0 ? 'border-[var(--trace-failure)]' : 'border-border')}
    >
      <header className="flex flex-wrap items-center gap-x-1.5 gap-y-1 sm:gap-x-2">
        <span className="font-mono text-[11px] text-muted-foreground" aria-hidden="true">{position}</span>
        <span className="flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] font-medium" style={{ color: kind.color, backgroundColor: `color-mix(in srgb, ${kind.color} 14%, transparent)` }}>
          <Icon aria-hidden="true" className="h-3 w-3" />
          {kind.label}
        </span>
        {badge && <span className="rounded border border-border px-1.5 py-0.5 text-[10px] uppercase tracking-wider text-muted-foreground">{badge}</span>}
        <h4 id={headingId} className="order-last min-w-0 basis-full truncate text-xs text-foreground sm:order-none sm:basis-auto sm:flex-1">
          <span className="sr-only">Step {position}: </span>{describeStep(step, catalog)}
        </h4>
        <StepIdField id={step.id} />
        {comments && comments.savedStepIds.includes(step.id) && (
          <StepCommentsToggle projectId={comments.projectId} scenarioId={comments.scenarioId} stepId={step.id} open={showComments} onToggle={() => setShowComments((v) => !v)} />
        )}
        <Button type="button" variant="ghost" size="icon" className="h-6 w-6" disabled={position === 1} onClick={() => onMove(-1)} aria-label={`Move step ${step.id} up`}>
          <ArrowUp className="h-3.5 w-3.5" />
        </Button>
        <Button type="button" variant="ghost" size="icon" className="h-6 w-6" disabled={position === count} onClick={() => onMove(1)} aria-label={`Move step ${step.id} down`}>
          <ArrowDown className="h-3.5 w-3.5" />
        </Button>
        <Button type="button" variant="ghost" size="icon" className="h-6 w-6 hover:text-[var(--trace-failure)]" onClick={onRemove} aria-label={`Remove step ${step.id}`}>
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </header>

      {problems.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 rounded-md bg-[color-mix(in_srgb,var(--trace-failure)_10%,transparent)] px-2 py-1.5" aria-label={`Problems with step ${step.id}`}>
          {problems.map((problem, i) => <li key={i} role="alert" className="text-[11px] leading-snug text-foreground">{problem}</li>)}
        </ul>
      )}

      <div className="mt-2">
        {step.type === 'call' && <CallFields step={step} available={available} onChange={onChange} />}
        {step.type === 'publish' && <PublishFields step={step} available={available} onChange={onChange} />}
        {step.type === 'write' && <WriteFields step={step} onChange={onChange} />}
        {step.type === 'parallel' && <ParallelFields step={step} available={available} onChange={onChange} />}
        {step.type === 'branch' && <BranchFields step={step} available={available} onChange={onChange} />}
      </div>

      {showComments && comments && comments.savedStepIds.includes(step.id) && (
        <div className="mt-2 border-t border-border pt-2">
          <TargetThreads projectId={comments.projectId} kind="step" id={comments.scenarioId} stepId={step.id} name={`${comments.scenarioName} · ${step.id}`} />
        </div>
      )}

    </article>
  )
}

/** Opens the comment threads of one saved step, with the number of open ones. */
function StepCommentsToggle({ projectId, scenarioId, stepId, open, onToggle }: {
  projectId: string; scenarioId: string; stepId: string; open: boolean; onToggle: () => void
}) {
  const count = useOpenThreadCounts(projectId)('step', scenarioId, stepId)
  return (
    <Button
      type="button" variant={open ? 'secondary' : 'ghost'} size="sm" className="h-6 gap-1 px-1.5 text-[11px]"
      aria-pressed={open} aria-label={`Comments on step ${stepId}${count > 0 ? `, ${count} open` : ''}`} onClick={onToggle}
    >
      <MessageSquare aria-hidden="true" className="h-3 w-3" />
      {count > 0 && <span>{count}</span>}
    </Button>
  )
}

/** The step ID, which branches and expected results name. It changes on Enter or when the field loses focus. */
function StepIdField({ id }: { id: string }) {
  const { rename } = useContext(StepEditingContext)
  const [text, setText] = useState(id)
  const [error, setError] = useState<string | null>(null)
  const errorId = useId()
  const commit = () => {
    const next = text.trim()
    if (next === id) return setError(null)
    const reason = rename(id, next)
    setError(reason)
    if (reason) setText(id)
  }
  return (
    <span className="relative ml-auto sm:ml-0">
      <input
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commit() } }}
        aria-label="Step ID"
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        spellCheck={false}
        maxLength={100}
        className="h-6 w-16 rounded border border-transparent sm:w-28 bg-transparent px-1 font-mono text-[11px] text-muted-foreground outline-none hover:border-input focus-visible:border-ring focus-visible:text-foreground focus-visible:ring-2 focus-visible:ring-ring/50"
      />
      {error && <span id={errorId} role="alert" className="absolute top-full right-0 z-10 mt-1 w-56 rounded border border-border bg-popover p-1.5 text-[11px] text-[var(--trace-failure)] shadow">{error}</span>}
    </span>
  )
}

function Grid({ children }: { children: ReactNode }) {
  return <div className="grid gap-2 sm:grid-cols-2">{children}</div>
}

/** A note about the contract a step uses, such as its timeout and retries. */
function ContractNote({ children }: { children: ReactNode }) {
  return <p className="mt-1.5 text-[11px] leading-relaxed text-muted-foreground">{children}</p>
}

function CallFields({ step, available, onChange }: { step: CallStep; available: string[]; onChange: (step: Step) => void }) {
  const catalog = useCatalog()
  const operation = catalog.operation(step.operation)
  const set = (fields: Partial<CallStep>) => onChange({ ...step, ...fields })
  const callee = operation ? catalog.name(operation.ownerId) : null

  return (
    <>
      <Grid>
        <ReferencePicker label="Caller" value={step.from || null} options={catalog.elements} onChange={(id) => set({ from: id ?? '' })} placeholder="Choose the element that calls" emptyText="The model has no elements" />
        <ReferencePicker label="Operation" value={step.operation || null} options={catalog.operations} onChange={(id) => set({ operation: id ?? '' })} placeholder="Choose the operation" emptyText="Add an operation under Contracts first" />
      </Grid>
      {operation && (
        <ContractNote>
          Served by {callee ?? 'an unknown element'}. {operation.timeoutMs ? `Times out after ${operation.timeoutMs} ms.` : 'No timeout.'}{' '}
          {operation.retry.maxAttempts > 1 ? `Tried ${operation.retry.maxAttempts} times, ${operation.retry.backoffMs} ms apart.` : 'Tried once.'}{' '}
          The timeout and the retries belong to the operation: change them under Contracts.
        </ContractNote>
      )}
      <div className="mt-2">
        <Grid>
          <JsonField label="Request" value={step.request} onChange={(request) => set({ request })} schema={catalog.schemaName(operation?.requestSchemaId ?? null)} />
          <JsonField label="Response" value={step.response} onChange={(response) => set({ response })} schema={catalog.schemaName(operation?.responseSchemaId ?? null)} />
        </Grid>
      </div>
      <StepList
        title={`Inside ${callee ?? 'the callee'}, before it responds`}
        steps={step.then} onChange={(then) => set({ then })} inherited={available}
        empty="Steps the callee runs. If one fails, the attempt fails and the call is tried again."
      />
      <StepList
        title="When every attempt fails"
        accent="var(--trace-failure)"
        steps={step.onError} onChange={(onError) => set({ onError })} inherited={available}
        empty="Steps the caller runs to handle the failure. Without them, the steps after this call are skipped."
      />
    </>
  )
}

function PublishFields({ step, available, onChange }: { step: PublishStep; available: string[]; onChange: (step: Step) => void }) {
  const catalog = useCatalog()
  const message = catalog.message(step.message)
  const set = (fields: Partial<PublishStep>) => onChange({ ...step, ...fields })
  const reached = step.channel ? catalog.reached(step.channel) : []
  const reachedIds = new Set(reached.map((r) => r.subscription.id))
  const strays = Object.keys(step.handlers).filter((id) => !reachedIds.has(id))
  const routingId = useId()

  const setHandler = (subscriptionId: string, steps: Step[] | null) => {
    const handlers = { ...step.handlers }
    if (steps) handlers[subscriptionId] = steps
    else delete handlers[subscriptionId]
    set({ handlers })
  }

  return (
    <>
      <Grid>
        <ReferencePicker
          label="Message" value={step.message || null} options={catalog.messages}
          // The declared producer is the usual publisher; a person can still choose another.
          onChange={(id) => set({ message: id ?? '', from: step.from || (id ? catalog.message(id)?.producerId ?? '' : '') })}
          placeholder="Choose the message" emptyText="Add a message under Contracts first"
        />
        <ReferencePicker label="Publisher" value={step.from || null} options={catalog.elements} onChange={(id) => set({ from: id ?? '' })} placeholder="Choose the element that publishes" />
        <ReferencePicker label="Channel" value={step.channel || null} options={catalog.channels} onChange={(id) => set({ channel: id ?? '' })} placeholder="Choose the channel" emptyText="Add a channel under Contracts first" />
        <div>
          <label htmlFor={routingId} className="block text-[11px] font-medium text-muted-foreground">Routing key (optional)</label>
          <Input
            id={routingId} className="mt-1 h-8 font-mono text-xs" value={step.routingKey ?? ''} maxLength={200} placeholder="order.created"
            onChange={(e) => {
              const { routingKey: _drop, ...rest } = step
              void _drop
              onChange(e.target.value ? { ...rest, routingKey: e.target.value } : rest)
            }}
          />
        </div>
      </Grid>
      {message && message.producerId !== step.from && step.from && (
        <ContractNote>The model declares {catalog.name(message.producerId) ?? 'another element'} as the producer of {message.name}.</ContractNote>
      )}
      <div className="mt-2">
        <JsonField label="Payload" value={step.payload} onChange={(payload) => set({ payload })} schema={catalog.schemaName(message?.schemaId ?? null)} />
      </div>
      <HeadersField headers={step.headers} onChange={(headers) => set({ headers })} />

      <section className="mt-2 border-l-2 pl-3" style={{ borderColor: 'var(--trace-event)' }} aria-label="Consumers">
        <h4 className="mb-1 text-[11px] font-medium text-muted-foreground">Consumers</h4>
        {!step.channel && <p className="text-xs text-muted-foreground">Choose a channel to see the subscriptions it delivers to.</p>}
        {step.channel && reached.length === 0 && <p className="text-xs text-muted-foreground">No subscription listens to this channel or to a channel its bindings route to.</p>}
        <ul className="space-y-2">
          {reached.map(({ subscription, label, detail }) => {
            const handler = step.handlers[subscription.id]
            return (
              <li key={subscription.id}>
                <div className="flex items-start gap-2">
                  <div className="min-w-0 flex-1">
                    <p className="text-xs text-foreground">{label}</p>
                    <p className="font-mono text-[10px] text-muted-foreground">{detail}</p>
                  </div>
                  {handler ? (
                    <Button type="button" variant="ghost" size="sm" className="h-6 px-1.5 text-xs" onClick={() => setHandler(subscription.id, null)} aria-label={`Remove the steps of ${label}`}>
                      <X className="h-3 w-3" /> Remove steps
                    </Button>
                  ) : (
                    <Button type="button" variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setHandler(subscription.id, [])}>
                      <Plus className="h-3 w-3" /> Add steps
                    </Button>
                  )}
                </div>
                {handler && (
                  <StepList
                    title={`Steps ${catalog.name(subscription.consumerId) ?? 'the consumer'} runs`}
                    accent="var(--trace-event)"
                    steps={handler} onChange={(steps) => setHandler(subscription.id, steps)} inherited={available}
                    empty="If a step fails, the delivery is tried again, then dead-lettered."
                  />
                )}
              </li>
            )
          })}
          {strays.map((id) => (
            <li key={id} className="flex items-center gap-2">
              <p role="alert" className="min-w-0 flex-1 text-xs text-[var(--trace-failure)]">
                {catalog.name(id) ?? 'A subscription that is not in the model'} does not receive this channel. Its steps will not run.
              </p>
              <Button type="button" variant="ghost" size="sm" className="h-6 px-1.5 text-xs" onClick={() => setHandler(id, null)}>Remove</Button>
            </li>
          ))}
        </ul>
        <ContractNote>Every subscription receives the message. Retries and the dead-letter channel belong to the subscription: change them under Contracts.</ContractNote>
      </section>
    </>
  )
}

function WriteFields({ step, onChange }: { step: WriteStep; onChange: (step: Step) => void }) {
  const catalog = useCatalog()
  const set = (fields: Partial<WriteStep>) => onChange({ ...step, ...fields })
  const datasetId = useId()
  // Stores come first: a write usually targets one.
  const stores = [...catalog.elements].sort((a, b) => Number(b.detail === 'store') - Number(a.detail === 'store'))
  return (
    <>
      <Grid>
        <ReferencePicker label="Writer" value={step.by || null} options={catalog.elements} onChange={(id) => set({ by: id ?? '' })} placeholder="Choose the element that writes" />
        <ReferencePicker label="Data store" value={step.store || null} options={stores} onChange={(id) => set({ store: id ?? '' })} placeholder="Choose the store" />
        <div>
          <label htmlFor={datasetId} className="block text-[11px] font-medium text-muted-foreground">Dataset</label>
          <Input id={datasetId} className="mt-1 h-8 text-sm" value={step.dataset} maxLength={200} placeholder="orders" onChange={(e) => set({ dataset: e.target.value })} aria-invalid={step.dataset.trim() ? undefined : true} />
        </div>
      </Grid>
      <div className="mt-2">
        <JsonField label="Change" value={step.change} onChange={(change) => set({ change })} />
      </div>
    </>
  )
}

function ParallelFields({ step, available, onChange }: { step: ParallelStep; available: string[]; onChange: (step: Step) => void }) {
  const setBranch = (index: number, steps: Step[]) => onChange({ ...step, branches: step.branches.map((b, i) => (i === index ? steps : b)) })
  return (
    <>
      {step.branches.map((branch, index) => (
        <div key={index} className="relative">
          <StepList title={`Branch ${index + 1}`} steps={branch} onChange={(steps) => setBranch(index, steps)} inherited={available} empty="An empty branch ends at once." />
          <Button
            type="button" variant="ghost" size="icon" className="absolute top-1.5 right-0 h-6 w-6 hover:text-[var(--trace-failure)]"
            onClick={() => onChange({ ...step, branches: step.branches.filter((_, i) => i !== index) })} aria-label={`Remove branch ${index + 1}`}
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      ))}
      <Button type="button" variant="ghost" size="sm" className="mt-1.5 h-6 gap-1 px-1.5 text-xs" onClick={() => onChange({ ...step, branches: [...step.branches, []] })}>
        <Plus className="h-3 w-3" /> Add branch
      </Button>
    </>
  )
}

function BranchFields({ step, available, onChange }: { step: BranchStep; available: string[]; onChange: (step: Step) => void }) {
  const { stepSummary } = useContext(StepEditingContext)
  const set = (fields: Partial<BranchStep>) => onChange({ ...step, ...fields })
  const outcomeId = useId()
  const options = available.map((id) => ({ id, label: id, detail: stepSummary(id) }))
  return (
    <>
      <Grid>
        <ReferencePicker
          label="When step" value={step.whenStep || null} options={options} onChange={(id) => set({ whenStep: id ?? '' })}
          placeholder="Choose an earlier step" emptyText="No step finishes before this one in the same flow"
        />
        <div>
          <label htmlFor={outcomeId} className="block text-[11px] font-medium text-muted-foreground">Is</label>
          <select id={outcomeId} className={cn(selectClass, 'mt-1')} value={step.is} onChange={(e) => set({ is: e.target.value as Outcome })}>
            {OUTCOMES.map((o) => <option key={o} value={o}>{o}</option>)}
          </select>
        </div>
      </Grid>
      {step.whenStep && !available.includes(step.whenStep) && (
        <p role="alert" className="mt-1 text-[11px] text-[var(--trace-failure)]">Step {step.whenStep} is not final when this branch runs. Choose a step that finishes earlier in the same flow.</p>
      )}
      <StepList title="Then" steps={step.then} onChange={(then) => set({ then })} inherited={available} empty={`Steps that run when ${step.whenStep || 'the step'} is ${step.is.toLowerCase()}.`} />
      <StepList title="Else" steps={step.else} onChange={(els) => set({ else: els })} inherited={available} empty="Steps that run otherwise." />
    </>
  )
}

/**
 * A JSON value: a payload, a request, a response or a change. Empty means null. Text that does
 * not parse stays in the field and is reported, so the document is not saved with it.
 */
function JsonField({ label, value, onChange, schema }: { label: string; value: unknown; onChange: (value: unknown) => void; schema?: string | null }) {
  const { reportJson } = useContext(StepEditingContext)
  const key = useId()
  const [text, setText] = useState(() => (value === null || value === undefined ? '' : JSON.stringify(value, null, 2)))
  const [error, setError] = useState<string | null>(null)
  const errorId = `${key}-error`

  useEffect(() => {
    reportJson(key, error !== null)
    return () => reportJson(key, false)
  }, [key, error, reportJson])

  const edit = (next: string) => {
    setText(next)
    if (!next.trim()) {
      setError(null)
      onChange(null)
      return
    }
    try {
      onChange(JSON.parse(next))
      setError(null)
    } catch {
      setError('This is not valid JSON.')
    }
  }

  return (
    <div className="min-w-0">
      <label htmlFor={key} className="flex items-baseline justify-between gap-2 text-[11px] font-medium text-muted-foreground">
        <span>{label} <span className="font-normal">(JSON, optional)</span></span>
        {schema && <span className="truncate font-normal">schema {schema}</span>}
      </label>
      <textarea
        id={key}
        value={text}
        onChange={(e) => edit(e.target.value)}
        spellCheck={false}
        rows={Math.min(8, Math.max(2, text.split('\n').length))}
        placeholder={schema ? 'The schema needs a value' : 'Empty: no value'}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className="mt-1 w-full resize-y rounded-lg border border-input bg-background p-2 font-mono text-[11px] leading-relaxed text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive"
      />
      {error && <p id={errorId} role="alert" className="text-[11px] text-[var(--trace-failure)]">{error}</p>}
    </div>
  )
}

export function HeadersField({ headers, onChange }: { headers: Record<string, string>; onChange: (headers: Record<string, string>) => void }) {
  const entries = Object.entries(headers)
  const groupId = useId()
  const rename = (from: string, to: string) => onChange(Object.fromEntries(entries.map(([k, v]) => [k === from ? to : k, v])))
  const add = () => {
    let n = entries.length + 1
    while (`x-header-${n}` in headers) n++
    onChange({ ...headers, [`x-header-${n}`]: '' })
  }
  return (
    <div className="mt-2" role="group" aria-labelledby={groupId}>
      <p id={groupId} className="text-[11px] font-medium text-muted-foreground">Headers</p>
      <ul className="mt-1 space-y-1">
        {entries.map(([name, value], index) => (
          // The name input keeps its own text until it loses focus, so a row is keyed by the
          // header it edits: a row keyed by position would keep the text of a removed header.
          <li key={name} className="flex items-center gap-1.5">
            <Input
              aria-label={`Header ${index + 1} name`} className="h-7 w-40 font-mono text-xs" defaultValue={name} maxLength={200}
              onBlur={(e) => { const next = e.target.value.trim(); if (next && next !== name && !(next in headers)) rename(name, next); else e.target.value = name }}
            />
            <Input aria-label={`Header ${name} value`} className="h-7 flex-1 font-mono text-xs" value={value} maxLength={2000} onChange={(e) => onChange({ ...headers, [name]: e.target.value })} />
            <Button type="button" variant="ghost" size="icon" className="h-6 w-6" aria-label={`Remove header ${name}`} onClick={() => onChange(Object.fromEntries(entries.filter(([k]) => k !== name)))}>
              <X className="h-3 w-3" />
            </Button>
          </li>
        ))}
      </ul>
      <Button type="button" variant="ghost" size="sm" className="mt-1 h-6 gap-1 px-1.5 text-xs" onClick={add} disabled={entries.length >= 50}>
        <Plus className="h-3 w-3" /> Add header
      </Button>
    </div>
  )
}
