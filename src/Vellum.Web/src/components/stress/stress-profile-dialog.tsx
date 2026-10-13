import { useState, type ReactNode } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { loadTestsApi, measurementRef } from '@/lib/load-tests-api'
import {
  emptyProfile, stressApi, type StressProfileDefaults,
  type ArrivalKind, type Distribution, type DistributionKind, type StressProfile, type StressProfileDetail, type StressRequirements,
} from '@/lib/stress-api'

interface Props {
  projectId: string
  branchId?: string | null
  scenarios: { id: string; name: string }[]
  /** Null for a new profile. */
  existing: StressProfileDetail | null
  /** What a new profile starts with. Not read for an existing profile. */
  defaults: StressProfileDefaults
  onClose: () => void
  /** Called with the saved profile; `run` asks the caller to run it at once. */
  onSaved: (saved: StressProfileDetail, run: boolean) => void
}

const selectClass =
  'h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30'

const ARRIVALS: { kind: ArrivalKind; label: string; help: string }[] = [
  { kind: 'constant', label: 'Constant', help: 'One flow starts every 1 / rate seconds.' },
  { kind: 'poisson', label: 'Poisson', help: 'Flows start at random; the gaps have the mean 1 / rate.' },
  { kind: 'ramp', label: 'Ramp', help: 'Random starts. The rate moves in a straight line from the first rate to the second over the duration.' },
  { kind: 'burst', label: 'Burst windows', help: 'Random starts at the base rate. Inside a window the rate of the window applies.' },
]

const DISTRIBUTIONS: { kind: DistributionKind; label: string; fields: { key: keyof Distribution; label: string }[] }[] = [
  { kind: 'fixed', label: 'Fixed', fields: [{ key: 'ms', label: 'Time, ms' }] },
  { kind: 'exponential', label: 'Exponential', fields: [{ key: 'meanMs', label: 'Mean, ms' }] },
  { kind: 'lognormal', label: 'Lognormal', fields: [{ key: 'medianMs', label: 'Median, ms' }, { key: 'p95Ms', label: '95th percentile, ms' }] },
  { kind: 'uniform', label: 'Uniform', fields: [{ key: 'minMs', label: 'Minimum, ms' }, { key: 'maxMs', label: 'Maximum, ms' }] },
]

/** What a measured service time takes from the request latency of its k6 result, for each distribution. The server writes the numbers. */
const MEASURED_FROM: Record<DistributionKind, string> = {
  fixed: 'the mean',
  exponential: 'the mean',
  lognormal: 'the median (p50) and the 95th percentile',
  uniform: 'the minimum and the maximum',
}

/** A number input that gives null when it is empty. */
function NumberField({ label, value, onChange, min = 0, step = 'any', placeholder, className, disabled }: {
  label: string; value: number | null | undefined; onChange: (value: number | null) => void
  min?: number; step?: number | 'any'; placeholder?: string; className?: string; disabled?: boolean
}) {
  return (
    <label className={`block text-[11px] font-medium text-muted-foreground ${className ?? ''}`}>
      {label}
      <Input
        type="number" inputMode="decimal" min={min} step={step} placeholder={placeholder} disabled={disabled}
        className="mt-1 font-mono"
        value={value ?? ''}
        onChange={(e) => onChange(e.target.value === '' || Number.isNaN(e.target.valueAsNumber) ? null : e.target.valueAsNumber)}
      />
    </label>
  )
}

function Section({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <section className="space-y-2">
      <div>
        <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
        {hint && <p className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">{hint}</p>}
      </div>
      {children}
    </section>
  )
}

/** The entry of a list for one target, or a blank one. A blank entry is left out when the profile is saved. */
type Draft = Omit<StressProfile, 'resources' | 'subscriptions' | 'branches'> & {
  resources: Record<string, { concurrency: number | null; queueCapacity: number | null }>
  times: Record<string, Distribution>
  subscriptions: Record<string, { consumers: number | null; queueCapacity: number | null }>
  branches: Record<string, number | null>
  policies: Record<string, { timeoutMs: number | null; maxAttempts: number | null; backoffMs: number | null }>
  /** The measured result that a service time names as its source. No entry: the value is declared. */
  sources: Record<string, string>
}

function toDraft(profile: StressProfile): Draft {
  return {
    ...profile,
    resources: Object.fromEntries(profile.resources.map((r) => [r.elementId, { concurrency: r.concurrency, queueCapacity: r.queueCapacity }])),
    times: Object.fromEntries(profile.serviceTimes.map((t) => [t.target, t.time])),
    subscriptions: Object.fromEntries(profile.subscriptions.map((s) => [s.subscriptionId, { consumers: s.consumers, queueCapacity: s.queueCapacity }])),
    branches: Object.fromEntries(profile.branches.map((b) => [b.stepId, b.probability])),
    policies: Object.fromEntries(profile.overrides.map((o) => [o.target, { timeoutMs: o.timeoutMs, maxAttempts: o.maxAttempts, backoffMs: o.backoffMs }])),
    sources: Object.fromEntries(profile.serviceTimes.filter((t) => t.label === 'measured' && t.measurementRef).map((t) => [t.target, t.measurementRef!])),
  }
}

/** Only what the scenario uses and a person filled in. The run names every input that is still missing. */
function toProfile(draft: Draft, needs: StressRequirements | undefined): StressProfile {
  const complete = (time: Distribution | undefined) =>
    time !== undefined && DISTRIBUTIONS.find((d) => d.kind === time.kind)!.fields.every((f) => typeof time[f.key] === 'number')
  const used = <T,>(ids: string[] | undefined, map: Record<string, T>) => (ids ?? Object.keys(map)).filter((id) => map[id] !== undefined)

  return {
    name: draft.name.trim(),
    scenarioId: draft.scenarioId,
    seed: draft.seed.trim(),
    arrivals: {
      ...draft.arrivals,
      toRatePerSecond: draft.arrivals.kind === 'ramp' ? draft.arrivals.toRatePerSecond : null,
      bursts: draft.arrivals.kind === 'burst' ? (draft.arrivals.bursts ?? []) : null,
    },
    resources: used(needs?.resources.map((r) => r.elementId), draft.resources)
      .filter((id) => draft.resources[id].concurrency !== null)
      .map((id) => ({ elementId: id, concurrency: draft.resources[id].concurrency!, queueCapacity: draft.resources[id].queueCapacity })),
    serviceTimes: used(needs?.serviceTimes.map((t) => t.target), draft.times)
      .filter((id) => draft.sources[id] !== undefined || complete(draft.times[id]))
      .map((id) => {
        const time = draft.times[id]
        const source = draft.sources[id]
        // A measured service time sends its distribution and the name of the result. The server takes the numbers from the result.
        if (source) return { target: id, time: { kind: time.kind }, label: 'measured' as const, measurementRef: source }
        const fields = DISTRIBUTIONS.find((d) => d.kind === time.kind)!.fields
        return { target: id, time: { kind: time.kind, ...Object.fromEntries(fields.map((f) => [f.key, time[f.key]])) } }
      }),
    subscriptions: used(needs?.subscriptions.map((s) => s.subscriptionId), draft.subscriptions)
      .filter((id) => draft.subscriptions[id].consumers !== null)
      .map((id) => ({ subscriptionId: id, consumers: draft.subscriptions[id].consumers!, queueCapacity: draft.subscriptions[id].queueCapacity })),
    branches: used(needs?.branches, draft.branches)
      .filter((id) => draft.branches[id] !== null)
      .map((id) => ({ stepId: id, probability: draft.branches[id]! })),
    overrides: used(needs ? [...needs.serviceTimes.filter((t) => t.kind !== 'store').map((t) => t.target)] : undefined, draft.policies)
      .filter((id) => Object.values(draft.policies[id]).some((v) => v !== null))
      .map((id) => ({ target: id, ...draft.policies[id] })),
    outages: draft.outages,
  }
}

/**
 * The form of one stress profile. The scenario decides which inputs exist: every element
 * that does work, every service time, every reached subscription and every branch step.
 * No input of the scenario has a default; an empty field stays empty and the run says that it
 * is missing. The seed, the window and the arrival rate of a new profile start from the
 * workspace's simulation settings.
 */
export function StressProfileDialog({ projectId, branchId, scenarios, existing, defaults, onClose, onSaved }: Props) {
  const [draft, setDraft] = useState<Draft>(() => toDraft(existing?.profile ?? emptyProfile(scenarios[0]?.id ?? '', defaults)))
  const set = <K extends keyof Draft>(field: K, value: Draft[K]) => setDraft((d) => ({ ...d, [field]: value }))
  const arrivals = draft.arrivals
  const setArrivals = (change: Partial<Draft['arrivals']>) => set('arrivals', { ...arrivals, ...change })

  const needs = useQuery({
    queryKey: ['projects', projectId, 'stress', 'requirements', draft.scenarioId, { branchId: branchId ?? undefined }],
    queryFn: () => stressApi.requirements(projectId, draft.scenarioId, branchId),
    enabled: draft.scenarioId !== '',
    staleTime: 0,
  })
  const requirements = needs.data
  // The imported k6 results of the project: the only sources a measured value can name.
  const measured = useQuery({ queryKey: ['projects', projectId, 'load-tests', 'results'], queryFn: () => loadTestsApi.results(projectId) })

  const save = useMutation({
    mutationFn: async (run: boolean) => {
      const profile = toProfile(draft, requirements)
      const saved = existing ? await stressApi.update(projectId, existing.id, profile) : await stressApi.create(projectId, profile)
      return { saved, run }
    },
    onSuccess: ({ saved, run }) => onSaved(saved, run),
    onError: shownInline,
  })
  const fieldErrors = save.error instanceof ApiError ? save.error.fields : []

  const time = (target: string): Distribution => draft.times[target] ?? { kind: 'fixed' }
  const setTime = (target: string, change: Partial<Distribution>) => set('times', { ...draft.times, [target]: { ...time(target), ...change } })
  const policy = (target: string) => draft.policies[target] ?? { timeoutMs: null, maxAttempts: null, backoffMs: null }
  const setPolicy = (target: string, change: Partial<ReturnType<typeof policy>>) => set('policies', { ...draft.policies, [target]: { ...policy(target), ...change } })
  const bursts = arrivals.bursts ?? []
  const canSave = draft.name.trim() !== '' && draft.scenarioId !== '' && !save.isPending

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{existing ? `Stress profile · version ${existing.version}` : 'New stress profile'}</DialogTitle>
          <DialogDescription>
            A profile states the load and the capacity that one scenario is simulated under. Every number you enter is an assumption, and the result lists it as declared.
            A number that you type is never measured.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          <div className="grid gap-3 sm:grid-cols-3">
            <label className="block text-[11px] font-medium text-muted-foreground sm:col-span-2">
              Name
              <Input className="mt-1" value={draft.name} maxLength={200} onChange={(e) => set('name', e.target.value)} placeholder="Peak hour" />
            </label>
            <label className="block text-[11px] font-medium text-muted-foreground">
              Seed
              <Input className="mt-1 font-mono" value={draft.seed} inputMode="numeric" pattern="[0-9]*" onChange={(e) => set('seed', e.target.value.replace(/[^0-9]/g, ''))} />
            </label>
            <label className="block text-[11px] font-medium text-muted-foreground sm:col-span-3">
              Scenario
              <select className={`${selectClass} mt-1`} value={draft.scenarioId} onChange={(e) => set('scenarioId', e.target.value)}>
                {scenarios.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </label>
          </div>

          <Section title="Arrivals" hint={ARRIVALS.find((a) => a.kind === arrivals.kind)!.help}>
            <div className="grid gap-3 sm:grid-cols-4">
              <label className="block text-[11px] font-medium text-muted-foreground">
                Pattern
                <select className={`${selectClass} mt-1`} value={arrivals.kind} onChange={(e) => setArrivals({ kind: e.target.value as ArrivalKind })}>
                  {ARRIVALS.map((a) => <option key={a.kind} value={a.kind}>{a.label}</option>)}
                </select>
              </label>
              <NumberField
                label={arrivals.kind === 'ramp' ? 'Rate at the start, per second' : arrivals.kind === 'burst' ? 'Base rate, per second' : 'Rate, per second'}
                value={arrivals.ratePerSecond} onChange={(v) => setArrivals({ ratePerSecond: v ?? 0 })}
              />
              {arrivals.kind === 'ramp' && (
                <NumberField label="Rate at the end, per second" value={arrivals.toRatePerSecond} onChange={(v) => setArrivals({ toRatePerSecond: v })} />
              )}
              <NumberField label="Duration, s" value={arrivals.durationSeconds} onChange={(v) => setArrivals({ durationSeconds: v ?? 0 })} />
              <NumberField label="Warm-up, s" value={arrivals.warmUpSeconds} onChange={(v) => setArrivals({ warmUpSeconds: v ?? 0 })} />
            </div>
            {arrivals.kind === 'burst' && (
              <div className="space-y-1.5">
                {bursts.map((window, i) => (
                  <div key={i} className="grid grid-cols-[1fr_1fr_1fr_auto] items-end gap-2">
                    <NumberField label="From, s" value={window.fromSeconds} onChange={(v) => setArrivals({ bursts: bursts.map((w, j) => (j === i ? { ...w, fromSeconds: v ?? 0 } : w)) })} />
                    <NumberField label="To, s" value={window.toSeconds} onChange={(v) => setArrivals({ bursts: bursts.map((w, j) => (j === i ? { ...w, toSeconds: v ?? 0 } : w)) })} />
                    <NumberField label="Rate, per second" value={window.ratePerSecond} onChange={(v) => setArrivals({ bursts: bursts.map((w, j) => (j === i ? { ...w, ratePerSecond: v ?? 0 } : w)) })} />
                    <Button variant="ghost" size="sm" className="h-8 w-8 p-0" aria-label={`Remove burst window ${i + 1}`} onClick={() => setArrivals({ bursts: bursts.filter((_, j) => j !== i) })}><X className="h-3.5 w-3.5" /></Button>
                  </div>
                ))}
                <Button variant="secondary" size="sm" className="h-7 gap-1 text-xs" onClick={() => setArrivals({ bursts: [...bursts, { fromSeconds: 0, toSeconds: 0, ratePerSecond: 0 }] })}>
                  <Plus className="h-3 w-3" /> Add a window
                </Button>
              </div>
            )}
            <p className="text-[11px] text-muted-foreground">Flows that start during the warm-up are simulated and left out of the statistics.</p>
          </Section>

          {needs.isLoading && <p className="text-xs text-muted-foreground">Reading what the scenario uses…</p>}
          {needs.isError && <p role="alert" className="text-xs text-destructive">{needs.error.message}</p>}
          {requirements && requirements.scenarioIssues.length > 0 && (
            <div role="alert" className="rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs">
              <p className="font-medium text-foreground">The scenario has issues. Correct them before you run a stress profile.</p>
              <ul className="mt-1 list-disc space-y-0.5 pl-4 text-muted-foreground">
                {requirements.scenarioIssues.slice(0, 8).map((issue, i) => <li key={i}>{issue.stepId ? `${issue.stepId}: ` : ''}{issue.message}</li>)}
              </ul>
            </div>
          )}

          {requirements && requirements.resources.length > 0 && (
            <Section title="Workers" hint="An element serves one call or one write on each worker. A call that finds no free worker waits in the queue. A call that finds the queue full is rejected.">
              <ul className="space-y-2">
                {requirements.resources.map((resource) => {
                  const entry = draft.resources[resource.elementId] ?? { concurrency: null, queueCapacity: null }
                  const change = (next: Partial<typeof entry>) => set('resources', { ...draft.resources, [resource.elementId]: { ...entry, ...next } })
                  return (
                    <li key={resource.elementId} className="grid items-end gap-2 rounded-md border border-border p-2.5 sm:grid-cols-[1fr_9rem_11rem]">
                      <p className="self-center text-sm text-foreground">{resource.name}</p>
                      <NumberField label="Workers" min={1} step={1} value={entry.concurrency} onChange={(v) => change({ concurrency: v })} />
                      <NumberField label="Queue capacity" step={1} placeholder="no bound" value={entry.queueCapacity} onChange={(v) => change({ queueCapacity: v })} />
                    </li>
                  )
                })}
              </ul>
            </Section>
          )}

          {requirements && requirements.serviceTimes.length > 0 && (
            <Section title="Service times" hint="How long one piece of work holds a worker, without the steps it runs inside. Timeouts and retries come from the model. Fill a field under “Override” only to try another value; the result lists each override next to the value of the model.">
              <ul className="space-y-2">
                {requirements.serviceTimes.map((need) => {
                  const current = time(need.target)
                  const shape = DISTRIBUTIONS.find((d) => d.kind === current.kind)!
                  const overrides = policy(need.target)
                  const source = draft.sources[need.target]
                  // A k6 result measures an operation that its script drove, for the scenario of this profile. The server checks both.
                  const candidates = (measured.data ?? []).filter((r) => r.scenarioId === draft.scenarioId)
                  const chosen = (measured.data ?? []).find((r) => measurementRef(r.id) === source)
                  const stored = existing?.measurements.find((m) => m.target === need.target && m.measurementRef === source)
                  const staleBecause = source === undefined ? [] : chosen ? chosen.staleBecause : measured.data ? ['missing' as const] : (stored?.staleBecause ?? [])
                  return (
                    <li key={need.target} className="space-y-2 rounded-md border border-border p-2.5">
                      <div className="grid items-end gap-2 sm:grid-cols-[1fr_9rem_9rem_9rem]">
                        <p className="self-center text-sm text-foreground">{need.name}</p>
                        <label className="block text-[11px] font-medium text-muted-foreground">
                          Distribution
                          <select className={`${selectClass} mt-1`} value={current.kind} onChange={(e) => setTime(need.target, { kind: e.target.value as DistributionKind })}>
                            {DISTRIBUTIONS.map((d) => <option key={d.kind} value={d.kind}>{d.label}</option>)}
                          </select>
                        </label>
                        {shape.fields.map((field) => (
                          <NumberField
                            key={field.key} label={field.label} disabled={source !== undefined} placeholder={source !== undefined ? 'from the result' : undefined}
                            value={current[field.key] as number | null | undefined} onChange={(v) => setTime(need.target, { [field.key]: v })}
                          />
                        ))}
                      </div>
                      {need.kind === 'operation' && (candidates.length > 0 || source !== undefined) && (
                        <label className="block text-[11px] font-medium text-muted-foreground">
                          Source of this value
                          <select
                            className={`${selectClass} mt-1`} value={source ?? ''}
                            onChange={(e) => {
                              const picked = e.target.value
                              setDraft((d) => {
                                const sources = { ...d.sources }
                                delete sources[need.target]
                                if (!picked) return { ...d, sources }
                                // The numbers of a measured service time are not typed: they are the numbers of the result.
                                return { ...d, sources: { ...sources, [need.target]: picked }, times: { ...d.times, [need.target]: { kind: (d.times[need.target] ?? { kind: 'fixed' }).kind } } }
                              })
                            }}
                          >
                            <option value="">Declared: a person states it</option>
                            {source !== undefined && !candidates.some((r) => measurementRef(r.id) === source) && (
                              <option value={source}>Measured: {source}</option>
                            )}
                            {candidates.map((r) => (
                              <option key={r.id} value={measurementRef(r.id)}>
                                Measured: k6 run of {new Date(r.startedAt).toLocaleString()} against {r.provenance.targetName}{r.stale ? ' (stale)' : ''}
                              </option>
                            ))}
                          </select>
                        </label>
                      )}
                      {source !== undefined && (
                        <p className="text-[11px] leading-relaxed text-muted-foreground">
                          <span className="font-mono text-foreground">measured</span> · Vellum takes {MEASURED_FROM[current.kind]} of the request latency of this operation from the result when you save.
                          You cannot type these numbers. k6 measures the time from the request to the response, with the network and the wait at the target, so this is not a pure service time.
                        </p>
                      )}
                      {staleBecause.length > 0 && (
                        <p role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-2.5 py-1.5 text-[11px] leading-relaxed text-foreground">
                          {staleBecause.includes('missing')
                            ? 'Stale measurement: the measured result is not in the project.'
                            : `Stale measurement: the ${staleBecause.join(' and the ')} changed after the script of that run was generated.`}
                          {' '}A stale measurement does not raise the confidence of a run. Run a new script and choose its result, or choose “Declared”.
                        </p>
                      )}
                      {need.kind !== 'store' && (() => {
                        const declared = need.kind === 'operation'
                          ? need
                          : requirements.subscriptions.find((s) => s.subscriptionId === need.target)
                        return (
                          <div className="grid items-end gap-2 border-t border-border pt-2 sm:grid-cols-[1fr_9rem_9rem_9rem]">
                            <p className="self-center text-[11px] leading-relaxed text-muted-foreground">
                              Model: {need.kind === 'operation' ? `timeout ${need.timeoutMs ? `${need.timeoutMs} ms` : 'none'}, ` : ''}
                              {declared?.maxAttempts ?? 1} {declared?.maxAttempts === 1 ? 'attempt' : 'attempts'}, backoff {declared?.backoffMs ?? 0} ms
                            </p>
                            {need.kind === 'operation'
                              ? <NumberField label="Override timeout, ms" step={1} placeholder="model" value={overrides.timeoutMs} onChange={(v) => setPolicy(need.target, { timeoutMs: v })} />
                              : <span />}
                            <NumberField label="Override attempts" min={1} step={1} placeholder="model" value={overrides.maxAttempts} onChange={(v) => setPolicy(need.target, { maxAttempts: v })} />
                            <NumberField label="Override backoff, ms" step={1} placeholder="model" value={overrides.backoffMs} onChange={(v) => setPolicy(need.target, { backoffMs: v })} />
                          </div>
                        )
                      })()}
                    </li>
                  )
                })}
              </ul>
            </Section>
          )}

          {requirements && requirements.subscriptions.length > 0 && (
            <Section title="Consumers" hint="A subscription delivers one message on each consumer. A message that finds the queue full is dead-lettered, or dropped when the subscription has no dead-letter channel.">
              <ul className="space-y-2">
                {requirements.subscriptions.map((subscription) => {
                  const entry = draft.subscriptions[subscription.subscriptionId] ?? { consumers: null, queueCapacity: null }
                  const change = (next: Partial<typeof entry>) => set('subscriptions', { ...draft.subscriptions, [subscription.subscriptionId]: { ...entry, ...next } })
                  return (
                    <li key={subscription.subscriptionId} className="grid items-end gap-2 rounded-md border border-border p-2.5 sm:grid-cols-[1fr_9rem_11rem]">
                      <p className="self-center text-sm text-foreground">
                        {subscription.name}
                        <span className="block text-[11px] text-muted-foreground">{subscription.deadLetters ? 'has a dead-letter channel' : 'no dead-letter channel'}</span>
                      </p>
                      <NumberField label="Consumers" min={1} step={1} value={entry.consumers} onChange={(v) => change({ consumers: v })} />
                      <NumberField label="Queue capacity" step={1} placeholder="no bound" value={entry.queueCapacity} onChange={(v) => change({ queueCapacity: v })} />
                    </li>
                  )
                })}
              </ul>
            </Section>
          )}

          {requirements && requirements.branches.length > 0 && (
            <Section title="Branch probabilities" hint="A branch step reads the outcome of one playback. Under load it needs the share of executions that take its “then” path, from 0 to 1.">
              <div className="grid gap-2 sm:grid-cols-3">
                {requirements.branches.map((step) => (
                  <NumberField key={step} label={`Step ${step}`} step={0.01} value={draft.branches[step]} onChange={(v) => set('branches', { ...draft.branches, [step]: v })} />
                ))}
              </div>
            </Section>
          )}

          {requirements && requirements.outageTargets.length > 0 && (
            <Section title="Outages" hint="A window of virtual time in which a target is unavailable. A call or a delivery that reaches it in the window fails, and the retry policy applies.">
              {draft.outages.map((outage, i) => {
                const change = (next: Partial<typeof outage>) => set('outages', draft.outages.map((o, j) => (j === i ? { ...o, ...next } : o)))
                return (
                  <div key={i} className="grid grid-cols-[1fr_7rem_7rem_auto] items-end gap-2">
                    <label className="block text-[11px] font-medium text-muted-foreground">
                      Target
                      <select className={`${selectClass} mt-1`} value={outage.target} onChange={(e) => change({ target: e.target.value })}>
                        {requirements.outageTargets.map((t) => <option key={`${t.kind}:${t.id}`} value={t.id}>{t.name} ({t.kind})</option>)}
                      </select>
                    </label>
                    <NumberField label="From, s" value={outage.fromSeconds} onChange={(v) => change({ fromSeconds: v ?? 0 })} />
                    <NumberField label="To, s" value={outage.toSeconds} onChange={(v) => change({ toSeconds: v ?? 0 })} />
                    <Button variant="ghost" size="sm" className="h-8 w-8 p-0" aria-label={`Remove outage ${i + 1}`} onClick={() => set('outages', draft.outages.filter((_, j) => j !== i))}><X className="h-3.5 w-3.5" /></Button>
                  </div>
                )
              })}
              <Button variant="secondary" size="sm" className="h-7 gap-1 text-xs" onClick={() => set('outages', [...draft.outages, { target: requirements.outageTargets[0].id, fromSeconds: 0, toSeconds: 0 }])}>
                <Plus className="h-3 w-3" /> Add an outage
              </Button>
            </Section>
          )}

          {requirements && requirements.scenarioFaults > 0 && (
            <p className="text-[11px] text-muted-foreground">
              The scenario declares {requirements.scenarioFaults} {requirements.scenarioFaults === 1 ? 'fault' : 'faults'}. A stress run does not apply them. Use outage windows.
            </p>
          )}
        </div>

        {save.isError && (
          <div role="alert" className="text-xs text-destructive">
            <p>{save.error.message}</p>
            {fieldErrors.length > 0 && (
              <ul className="mt-1 list-disc pl-4">{fieldErrors.map((f, i) => <li key={i}><span className="font-mono">{f.field}</span>: {f.message}</li>)}</ul>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <p className="min-w-0 flex-1 text-[11px] text-muted-foreground">A value that you type is saved as <span className="font-mono">declared</span>. The service time of an operation is <span className="font-mono">measured</span> only when you choose an imported k6 result of this scenario as its source. Vellum then writes the numbers of that result.</p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button variant="secondary" size="sm" disabled={!canSave} onClick={() => save.mutate(false)}>Save</Button>
          <Button size="sm" disabled={!canSave} onClick={() => save.mutate(true)}>Save and run</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
