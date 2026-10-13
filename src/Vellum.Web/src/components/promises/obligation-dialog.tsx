import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { fieldMessages, shownInline } from '@/lib/mutation-errors'
import { fetchScenario, type ScenarioDocument } from '@/hooks/use-scenarios'
import { scenarioSteps } from '@/lib/scenario-parts'
import { TRACE_EVENT_KINDS, type TraceEventKind } from '@/lib/trace'
import {
  FAULT_KINDS, OBLIGATION_KINDS, STEP_OUTCOMES, promisesApi,
  type Assertion, type Fault, type Obligation, type ObligationInput, type PromiseDetail, type PromiseInput,
} from '@/lib/promises-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { Failure, Field, PatternFields, ReferenceSelect, areaClass, labelClass, smallSelect } from './promise-fields'

interface Props {
  projectId: string
  promiseId: string
  /** The obligation to change, or null to add one. */
  obligation: Obligation | null
  /** The forbidden events of the promise. A new scenario check asserts each of them. */
  forbiddenEvents: PromiseInput['forbiddenEvents']
  references: ScenarioReference[]
  scenarios: { id: string; name: string }[]
  repositories: string[]
  onStored: (promise: PromiseDetail) => void
  onClose: () => void
}

interface Form {
  kind: string
  title: string
  text: string
  scenarioId: string
  faults: Fault[]
  expect: Assertion[]
  pattern: { repository: string; pattern: string; service: string | null }
  requireUnconditional: boolean
  steps: string
  inputs: string
  expected: string
  forbidden: string
  contractIds: string[]
  evidence: { repository: string; workflow: string; job: string }
}

const lines = (items: string[] | undefined) => (items ?? []).join('\n')
const items = (text: string) => text.split('\n').map((line) => line.trim()).filter(Boolean)
const isPlan = (kind: string) => kind === 'contract-test' || kind === 'integration-test' || kind === 'failure-test'

function toForm(obligation: Obligation | null, repositories: string[]): Form {
  const recipe = obligation?.recipe
  const source = recipe?.plan?.evidenceSource
  return {
    kind: obligation?.kind ?? 'scenario-check',
    title: obligation?.title ?? '',
    text: recipe?.text ?? '',
    scenarioId: recipe?.scenarioCheck?.scenarioId ?? '',
    faults: recipe?.scenarioCheck?.faults ?? [],
    expect: recipe?.scenarioCheck?.expect ?? [],
    pattern: recipe?.sourcePattern
      ? { repository: recipe.sourcePattern.repository, pattern: recipe.sourcePattern.pattern, service: recipe.sourcePattern.service }
      : { repository: repositories[0] ?? '', pattern: 'transactional-outbox', service: null },
    requireUnconditional: recipe?.sourcePattern?.requireUnconditional ?? true,
    steps: lines(recipe?.plan?.steps), inputs: lines(recipe?.plan?.inputs),
    expected: lines(recipe?.plan?.expectedObservations), forbidden: lines(recipe?.plan?.forbiddenObservations),
    contractIds: recipe?.plan?.contractIds ?? [],
    evidence: { repository: source?.repository ?? '', workflow: source?.workflow ?? '', job: source?.job ?? '' },
  }
}

/** The assertions of a new scenario check: those of the scenario, and zero of each forbidden event of the promise. */
function defaultAssertions(scenario: ScenarioDocument, forbidden: PromiseInput['forbiddenEvents']): Assertion[] {
  const expect = (scenario.assertions as Assertion[]).map((a) => ({ ...a }))
  const taken = new Set(expect.map((a) => a.id))
  forbidden.forEach((event, i) => {
    let id = `forbidden-${i + 1}-${event.kind}`
    while (taken.has(id)) id += '-promise'
    taken.add(id)
    expect.push({ type: 'event-count', id, kind: event.kind as TraceEventKind, target: event.target, expected: 0 })
  })
  return expect
}

/**
 * One proof obligation of a draft: a check that Vellum runs, or a test plan whose result
 * comes from CI. Every part of the model is chosen by name; the server checks each one
 * and its refusal shows next to the field.
 */
export function ObligationDialog({ projectId, promiseId, obligation, forbiddenEvents, references, scenarios, repositories, onStored, onClose }: Props) {
  const queryClient = useQueryClient()
  const [form, setForm] = useState(() => toForm(obligation, repositories))
  const set = <K extends keyof Form>(field: K, value: Form[K]) => setForm((current) => ({ ...current, [field]: value }))
  const label = (id: string | null) => (id === null ? 'anything' : (references.find((r) => r.id === id)?.label ?? `${id.slice(0, 8)} (not in the model)`))

  const scenarioKey = (id: string) => ['projects', projectId, 'scenarios', id, 'document']
  const scenario = useQuery({
    queryKey: scenarioKey(form.scenarioId),
    queryFn: () => fetchScenario(projectId, form.scenarioId),
    enabled: form.kind === 'scenario-check' && form.scenarioId !== '',
  })
  const steps = scenario.data ? scenarioSteps(scenario.data.steps) : []
  const ownFaults = ((scenario.data?.faults ?? []) as Fault[]).map((f) => f.target)
  // What a fault of this check may target: every part a step of the scenario names, less those the scenario faults itself.
  const targetIds = [...new Set(steps.flatMap((s) => [...s.contractIds, ...s.elementIds]))]
  const targets = references.filter((r) => targetIds.includes(r.id) && !ownFaults.includes(r.id))
  const freeTargets = targets.filter((t) => !form.faults.some((f) => f.target === t.id))

  const chooseScenario = async (scenarioId: string) => {
    set('scenarioId', scenarioId)
    if (!scenarioId) return
    // A check of another scenario starts from that scenario's assertions: the old ones name its steps.
    const document = await queryClient.fetchQuery({ queryKey: scenarioKey(scenarioId), queryFn: () => fetchScenario(projectId, scenarioId) }).catch(() => null)
    if (document) setForm((current) => (current.scenarioId === scenarioId ? { ...current, faults: [], expect: defaultAssertions(document, forbiddenEvents) } : current))
  }

  const scenarioName = scenarios.find((s) => s.id === form.scenarioId)?.name ?? 'the scenario'
  const defaults: Record<string, { title: string; text: string }> = {
    'scenario-check': {
      title: form.faults.length > 0 ? `Scenario '${scenarioName}' holds with ${form.faults.map((f) => `${f.kind.toLowerCase()} of ${label(f.target)}`).join(' and ')}` : `Scenario '${scenarioName}' holds on the declared design`,
      text: `Play scenario '${scenarioName}' on a pinned model revision with its declared faults and the faults of this check. All ${form.expect.length} assertion(s) must hold.`,
    },
    'source-pattern-check': {
      title: `${form.pattern.service ? label(form.pattern.service) : form.pattern.repository || 'The repository'} declares the ${form.pattern.pattern} pattern`,
      text: `Look at one import snapshot of ${form.pattern.repository || 'the repository'}. A fact with pattern '${form.pattern.pattern}'${form.pattern.service ? ` for service '${label(form.pattern.service)}'` : ''} must exist at that commit.`,
    },
  }
  const fallback = defaults[form.kind] ?? { title: '', text: '' }

  const input = (): ObligationInput => ({
    kind: form.kind,
    title: (form.title.trim() || fallback.title).slice(0, 200),
    recipe: {
      text: form.text.trim() || fallback.text,
      ...(form.kind === 'scenario-check' && { scenarioCheck: { scenarioId: form.scenarioId, faults: form.faults, expect: form.expect } }),
      ...(form.kind === 'source-pattern-check' && {
        sourcePattern: { ...form.pattern, repository: form.pattern.repository.trim(), requireUnconditional: form.requireUnconditional },
      }),
      ...(isPlan(form.kind) && {
        plan: {
          steps: items(form.steps), inputs: items(form.inputs), expectedObservations: items(form.expected), forbiddenObservations: items(form.forbidden),
          contractIds: form.contractIds,
          // A plan with no workflow named has no evidence source. It can receive no result until a revision names one.
          evidenceSource: form.evidence.repository.trim() || form.evidence.workflow.trim()
            ? { system: 'github-actions', repository: form.evidence.repository.trim(), workflow: form.evidence.workflow.trim(), job: form.evidence.job.trim() || null }
            : null,
        },
      }),
    },
  })
  const save = useMutation({
    mutationFn: () => (obligation ? promisesApi.changeObligation(projectId, promiseId, obligation.key, input()) : promisesApi.addObligation(projectId, promiseId, input())),
    onSuccess: (promise) => { onStored(promise); onClose() },
    onError: shownInline,
  })
  const error = save.error
  const ready = form.kind === 'scenario-check' ? form.scenarioId !== '' && form.expect.length > 0
    : form.kind === 'source-pattern-check' ? form.pattern.repository.trim() !== '' && form.pattern.pattern !== ''
    : form.title.trim() !== '' && form.text.trim() !== '' && items(form.steps).length > 0 && items(form.expected).length > 0

  const setFault = (index: number, change: Partial<Fault>) => set('faults', form.faults.map((f, i) => (i === index ? { ...f, ...change } : f)))
  const setAssertion = (index: number, next: Assertion) => set('expect', form.expect.map((a, i) => (i === index ? next : a)))
  const newAssertionId = () => {
    for (let n = form.expect.length + 1; ; n++) if (!form.expect.some((a) => a.id === `check-${n}`)) return `check-${n}`
  }
  const contracts = references.filter((r) => r.group !== 'Elements')
  const kindHelp = OBLIGATION_KINDS.find((k) => k.kind === form.kind)?.help

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader className="pr-8">
          <DialogTitle>{obligation ? <>Change <span className="font-mono">{obligation.id}</span></> : 'Add an obligation'}</DialogTitle>
          <DialogDescription>
            An obligation is one check that gives evidence for the promise. It never holds a result: a result comes from a run or from your CI.
          </DialogDescription>
        </DialogHeader>

        <form
          id="obligation-form" className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1"
          onSubmit={(e) => { e.preventDefault(); e.stopPropagation(); save.mutate() }}
        >
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Kind" field="kind" error={error} hint={kindHelp}>
              {(control) => (
                <select
                  {...control} data-autofocus className={cn(smallSelect, 'mt-1')} value={form.kind}
                  // The kind of a stored obligation stays: its recipe has the shape of its kind.
                  disabled={obligation !== null} onChange={(e) => set('kind', e.target.value)}
                >
                  {!OBLIGATION_KINDS.some((k) => k.kind === form.kind) && <option value={form.kind}>{form.kind}</option>}
                  {OBLIGATION_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
                </select>
              )}
            </Field>
            <Field label="Title" field="title" error={error}>
              {(control) => <Input {...control} className="mt-1 h-7 text-xs" value={form.title} placeholder={fallback.title} maxLength={200} onChange={(e) => set('title', e.target.value)} />}
            </Field>
            <Field label="What the check does" field="recipe.text" error={error} className="sm:col-span-2" hint={fallback.text ? 'Leave the title and this text empty to use the text shown in them.' : undefined}>
              {(control) => <textarea {...control} className={areaClass} value={form.text} placeholder={fallback.text} onChange={(e) => set('text', e.target.value)} />}
            </Field>
          </div>

          {form.kind === 'scenario-check' && (
            <>
              <Field label="Scenario" field="recipe.scenarioCheck.scenarioId" error={error} hint="The check plays this scenario on the model and evaluates the assertions below.">
                {(control) => (
                  <select {...control} className={cn(smallSelect, 'mt-1 sm:max-w-sm')} value={form.scenarioId} onChange={(e) => void chooseScenario(e.target.value)}>
                    {(form.scenarioId === '' || !scenarios.some((s) => s.id === form.scenarioId)) && <option value={form.scenarioId}>{form.scenarioId ? 'No longer exists' : 'Choose a scenario…'}</option>}
                    {scenarios.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                  </select>
                )}
              </Field>
              {scenario.isError && <p role="alert" className="text-xs text-destructive">{scenario.error.message}</p>}

              {form.scenarioId !== '' && (
                <fieldset className="min-w-0" aria-describedby="faults-help">
                  <legend className={labelClass}>Faults</legend>
                  <p id="faults-help" className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
                    A fault makes attempts against one part fail. The check adds its faults to those of the scenario. It cannot change a fault that the scenario declares.
                    {ownFaults.length > 0 && ` The scenario already faults ${ownFaults.map(label).join(', ')}.`}
                  </p>
                  <ul className="mt-1.5 space-y-1.5">
                    {form.faults.map((fault, i) => {
                      const messages = fieldMessages(error, `recipe.scenarioCheck.faults[${i}]`)
                      return (
                        <li key={i}>
                          <div className="grid grid-cols-[1fr_auto] gap-1.5 sm:grid-cols-[minmax(0,1fr)_7rem_9rem_auto]">
                            <select className={cn(smallSelect, 'max-sm:col-span-2')} aria-label={`Fault ${i + 1}: target`} aria-invalid={messages.length > 0 ? true : undefined} value={fault.target} onChange={(e) => setFault(i, { target: e.target.value })}>
                              {!targets.some((t) => t.id === fault.target) && <option value={fault.target}>{label(fault.target)}</option>}
                              {targets.filter((t) => t.id === fault.target || freeTargets.includes(t)).map((t) => <option key={t.id} value={t.id}>{t.label} ({t.group.slice(0, -1).toLowerCase()})</option>)}
                            </select>
                            <select className={smallSelect} aria-label={`Fault ${i + 1}: kind`} value={fault.kind} onChange={(e) => setFault(i, { kind: e.target.value as Fault['kind'] })}>
                              {FAULT_KINDS.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
                            </select>
                            <label className="flex items-center gap-1.5 text-[11px] text-muted-foreground max-sm:col-span-2">
                              <span className="shrink-0">Fails the first</span>
                              <Input
                                className="h-7 w-14 px-1.5 text-xs" type="number" min={1} aria-label={`Fault ${i + 1}: number of first attempts that fail. Empty fails every attempt.`} placeholder="all"
                                value={fault.failFirst ?? ''} onChange={(e) => setFault(i, { failFirst: e.target.value === '' ? null : Math.max(1, Number(e.target.value)) })}
                              />
                            </label>
                            <Button type="button" variant="ghost" size="sm" className="h-7 w-7 p-0 max-sm:col-start-2 max-sm:row-start-2" aria-label={`Remove fault ${i + 1}`} onClick={() => set('faults', form.faults.filter((_, j) => j !== i))}>
                              <Trash2 className="h-3.5 w-3.5" />
                            </Button>
                          </div>
                          {messages.length > 0 && <p role="alert" className="mt-1 text-xs text-destructive">{messages.join(' ')}</p>}
                        </li>
                      )
                    })}
                  </ul>
                  {fieldMessages(error, 'recipe.scenarioCheck.faults').length > 0 && form.faults.length === 0 && (
                    <p role="alert" className="mt-1 text-xs text-destructive">{fieldMessages(error, 'recipe.scenarioCheck.faults').join(' ')}</p>
                  )}
                  <Button
                    type="button" variant="secondary" size="sm" className="mt-1.5 h-7 text-xs" disabled={freeTargets.length === 0}
                    title={freeTargets.length === 0 ? 'Every part that the steps of the scenario name has a fault' : undefined}
                    onClick={() => set('faults', [...form.faults, { target: (freeTargets.find((t) => t.group === 'Channels') ?? freeTargets[0]).id, kind: 'Outage', failFirst: 1 }])}
                  >
                    Add a fault
                  </Button>
                </fieldset>
              )}

              {form.scenarioId !== '' && (
                <fieldset className="min-w-0" aria-describedby="assertions-help">
                  <legend className={labelClass}>Assertions</legend>
                  <p id="assertions-help" className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
                    The check passes when every assertion holds. A run names each assertion that holds with and without the faults: it shows nothing.
                  </p>
                  <ul className="mt-1.5 space-y-1.5">
                    {form.expect.map((assertion, i) => {
                      const messages = fieldMessages(error, `recipe.scenarioCheck.expect[${i}]`)
                      return (
                        <li key={i} className="rounded-md border border-border p-2">
                          <div className="grid grid-cols-[1fr_auto] gap-1.5 sm:grid-cols-[9rem_minmax(0,1fr)_auto]">
                            <select
                              className={smallSelect} aria-label={`Assertion ${i + 1}: type`} value={assertion.type}
                              onChange={(e) => setAssertion(i, e.target.value === 'outcome'
                                ? { type: 'outcome', id: assertion.id, step: steps[0]?.id ?? '', expected: 'Succeeded' }
                                : { type: 'event-count', id: assertion.id, kind: 'Dropped', target: null, expected: 0 })}
                            >
                              <option value="outcome">Outcome of a step</option>
                              <option value="event-count">Count of an event</option>
                            </select>
                            <Input className="h-7 font-mono text-xs max-sm:col-span-2 max-sm:row-start-2" aria-label={`Assertion ${i + 1}: ID`} value={assertion.id} spellCheck={false} onChange={(e) => setAssertion(i, { ...assertion, id: e.target.value })} />
                            <Button type="button" variant="ghost" size="sm" className="h-7 w-7 p-0" aria-label={`Remove assertion ${i + 1}`} onClick={() => set('expect', form.expect.filter((_, j) => j !== i))}>
                              <Trash2 className="h-3.5 w-3.5" />
                            </Button>
                          </div>
                          {assertion.type === 'outcome' ? (
                            <div className="mt-1.5 grid gap-1.5 sm:grid-cols-[minmax(0,1fr)_9rem]">
                              <select className={smallSelect} aria-label={`Assertion ${i + 1}: step`} value={assertion.step} onChange={(e) => setAssertion(i, { ...assertion, step: e.target.value })}>
                                {!steps.some((s) => s.id === assertion.step) && <option value={assertion.step}>{assertion.step || 'Choose a step…'}</option>}
                                {steps.map((s) => <option key={s.id} value={s.id}>{s.id} ({s.type})</option>)}
                              </select>
                              <select className={smallSelect} aria-label={`Assertion ${i + 1}: expected outcome`} value={assertion.expected} onChange={(e) => setAssertion(i, { ...assertion, expected: e.target.value as (typeof STEP_OUTCOMES)[number] })}>
                                {STEP_OUTCOMES.map((outcome) => <option key={outcome} value={outcome}>{outcome}</option>)}
                              </select>
                            </div>
                          ) : (
                            <div className="mt-1.5 grid gap-1.5 sm:grid-cols-[9rem_minmax(0,1fr)_7rem]">
                              <select className={smallSelect} aria-label={`Assertion ${i + 1}: event`} value={assertion.kind} onChange={(e) => setAssertion(i, { ...assertion, kind: e.target.value as TraceEventKind })}>
                                {TRACE_EVENT_KINDS.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
                              </select>
                              <ReferenceSelect aria-label={`Assertion ${i + 1}: where`} empty="Anywhere in the scenario" value={assertion.target} references={references} onChange={(target) => setAssertion(i, { ...assertion, target })} />
                              <label className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
                                <span className="shrink-0">Exactly</span>
                                <Input className="h-7 w-14 px-1.5 text-xs" type="number" min={0} aria-label={`Assertion ${i + 1}: expected count`} value={assertion.expected} onChange={(e) => setAssertion(i, { ...assertion, expected: Math.max(0, Number(e.target.value)) })} />
                              </label>
                            </div>
                          )}
                          {messages.length > 0 && <p role="alert" className="mt-1 text-xs text-destructive">{messages.join(' ')}</p>}
                        </li>
                      )
                    })}
                  </ul>
                  {fieldMessages(error, 'recipe.scenarioCheck.expect').length > 0 && (
                    <p role="alert" className="mt-1 text-xs text-destructive">{[...new Set(fieldMessages(error, 'recipe.scenarioCheck.expect'))].join(' ')}</p>
                  )}
                  <div className="mt-1.5 flex flex-wrap gap-1.5">
                    <Button type="button" variant="secondary" size="sm" className="h-7 text-xs" disabled={steps.length === 0} onClick={() => set('expect', [...form.expect, { type: 'outcome', id: newAssertionId(), step: steps[0].id, expected: 'Succeeded' }])}>
                      Add an assertion
                    </Button>
                    {scenario.data && (
                      <Button type="button" variant="ghost" size="sm" className="h-7 text-xs" onClick={() => set('expect', defaultAssertions(scenario.data, forbiddenEvents))}>
                        Use the assertions of the scenario and the forbidden events
                      </Button>
                    )}
                  </div>
                </fieldset>
              )}
            </>
          )}

          {form.kind === 'source-pattern-check' && (
            <>
              <PatternFields value={form.pattern} onChange={(pattern) => set('pattern', pattern)} repositories={repositories} references={references} disabled={false} error={error} prefix="recipe.sourcePattern" />
              <label className="flex items-start gap-2 text-xs text-foreground">
                <input type="checkbox" className="mt-0.5 h-3.5 w-3.5 accent-[var(--primary)]" checked={form.requireUnconditional} onChange={(e) => set('requireUnconditional', e.target.checked)} />
                <span>
                  The pattern must be declared with no compile-time condition.
                  <span className="block text-[11px] text-muted-foreground">With this off, a declaration under <span className="font-mono">#if</span> passes, and the run says so.</span>
                </span>
              </label>
            </>
          )}

          {isPlan(form.kind) && (
            <>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Steps (one per line)" field="recipe.plan.steps" error={error}>
                  {(control) => <textarea {...control} className={areaClass} value={form.steps} onChange={(e) => set('steps', e.target.value)} />}
                </Field>
                <Field label="Inputs (one per line)" field="recipe.plan.inputs" error={error}>
                  {(control) => <textarea {...control} className={areaClass} value={form.inputs} onChange={(e) => set('inputs', e.target.value)} />}
                </Field>
                <Field label="Expected observations (one per line)" field="recipe.plan.expectedObservations" error={error}>
                  {(control) => <textarea {...control} className={areaClass} value={form.expected} onChange={(e) => set('expected', e.target.value)} />}
                </Field>
                <Field label="Forbidden observations (one per line)" field="recipe.plan.forbiddenObservations" error={error}>
                  {(control) => <textarea {...control} className={areaClass} value={form.forbidden} onChange={(e) => set('forbidden', e.target.value)} />}
                </Field>
              </div>
              <fieldset className="min-w-0">
                <legend className={labelClass}>Contracts under test</legend>
                {contracts.length === 0 && <p className="mt-1 text-xs text-muted-foreground">The model has no operation, channel, subscription or message.</p>}
                <ul className="mt-1 grid max-h-32 gap-x-4 gap-y-0.5 overflow-y-auto sm:grid-cols-2">
                  {contracts.map((contract) => (
                    <li key={contract.id}>
                      <label className="flex items-center gap-1.5 text-xs text-foreground">
                        <input
                          type="checkbox" className="h-3 w-3 accent-[var(--primary)]" checked={form.contractIds.includes(contract.id)}
                          onChange={() => set('contractIds', form.contractIds.includes(contract.id) ? form.contractIds.filter((id) => id !== contract.id) : [...form.contractIds, contract.id])}
                        />
                        <span className="truncate">{contract.label} <span className="text-muted-foreground">({contract.group.slice(0, -1).toLowerCase()})</span></span>
                      </label>
                    </li>
                  ))}
                </ul>
                {fieldMessages(error, 'recipe.plan.contractIds').length > 0 && <p role="alert" className="mt-1 text-xs text-destructive">{fieldMessages(error, 'recipe.plan.contractIds').join(' ')}</p>}
              </fieldset>
              <fieldset className="min-w-0" aria-describedby="evidence-source-help">
                <legend className={labelClass}>Evidence source</legend>
                <p id="evidence-source-help" className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
                  The result of this plan counts only when it comes from this GitHub Actions workflow, in a repository that this project imports. A plan with no evidence source can receive no result.
                </p>
                <div className="mt-1.5 grid gap-2 sm:grid-cols-3">
                  <Field label="Repository" field="recipe.plan.evidenceSource.repository" error={error}>
                    {(control) => repositories.length === 0 ? (
                      <Input {...control} className="mt-1 h-7 text-xs" placeholder="owner/name" spellCheck={false} value={form.evidence.repository} onChange={(e) => set('evidence', { ...form.evidence, repository: e.target.value })} />
                    ) : (
                      <select {...control} className={cn(smallSelect, 'mt-1')} value={form.evidence.repository} onChange={(e) => set('evidence', { ...form.evidence, repository: e.target.value })}>
                        <option value="">None</option>
                        {!repositories.includes(form.evidence.repository) && form.evidence.repository !== '' && <option value={form.evidence.repository}>{form.evidence.repository}</option>}
                        {repositories.map((repository) => <option key={repository} value={repository}>{repository}</option>)}
                      </select>
                    )}
                  </Field>
                  <Field label="Workflow" field="recipe.plan.evidenceSource.workflow" error={error}>
                    {(control) => <Input {...control} className="mt-1 h-7 text-xs" placeholder="ci.yml" spellCheck={false} value={form.evidence.workflow} onChange={(e) => set('evidence', { ...form.evidence, workflow: e.target.value })} />}
                  </Field>
                  <Field label="Job (optional)" field="recipe.plan.evidenceSource.job" error={error}>
                    {(control) => <Input {...control} className="mt-1 h-7 text-xs" spellCheck={false} value={form.evidence.job} onChange={(e) => set('evidence', { ...form.evidence, job: e.target.value })} />}
                  </Field>
                </div>
              </fieldset>
            </>
          )}
        </form>

        {error && (
          <Failure error={error} shown={['kind', 'title', 'recipe.text', 'recipe.scenarioCheck', 'recipe.sourcePattern', 'recipe.plan']} />
        )}

        <div className="flex items-center justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="obligation-form" size="sm" disabled={save.isPending || !ready}>
            {obligation ? 'Save the obligation' : 'Add the obligation'}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
