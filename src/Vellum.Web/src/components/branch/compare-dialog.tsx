import { useId, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { selectClass } from '@/components/settings/fields'
import { branchesApi, sideOf } from '@/lib/branches-api'
import { NOT_COMPARABLE, costsApi, describeError, formatAmount } from '@/lib/costs-api'
import { ENTITY_LABEL, ENTITY_ORDER, changedFields, entityLabel } from '@/lib/model-diff'
import { promisesApi, type BuildManifest } from '@/lib/promises-api'
import type { FindingsResponse } from '@/lib/review-api'
import { api } from '@/lib/settings-api'
import { stressApi, type StressComparison } from '@/lib/stress-api'
import type { ModelSelection } from '@/stores/draft-store'
import { ChangeChip, FieldTable } from './change-list'

interface Props {
  projectId: string
  /** Every model that can be a side: main, the open drafts, the variants and the revisions. */
  sides: ModelSelection[]
  /** The model the workbench shows. It starts as the right side when it is not main. */
  initial: ModelSelection
  onClose: () => void
}

const keyOf = (side: ModelSelection) => (side.kind === 'main' ? 'main' : `${side.kind}:${side.id}`)
const labelOf = (side: ModelSelection) => (side.kind === 'main' ? 'main' : `${side.kind[0].toUpperCase()}${side.kind.slice(1)}: ${side.name}`)
/** The query that names the model of a side for the endpoints that take a branch or a revision. */
const pinOf = (side: ModelSelection) => (side.kind === 'main' ? '' : side.kind === 'revision' ? `?revisionId=${side.id}` : `?branchId=${side.streamId}`)

const cell = 'px-2.5 py-1.5'
const numeric = `${cell} text-right font-mono tabular-nums`

/** A value that Vellum does not have. It is said in words, never left empty and never shown as zero. */
function Unknown({ children }: { children: ReactNode }) {
  return (
    <p className="rounded-md border border-dashed border-border px-2.5 py-2 text-xs text-muted-foreground">
      <span className="mr-1.5 rounded-full border border-[var(--trace-retry)] px-1.5 text-[11px] font-medium text-[var(--trace-retry)]">unknown</span>
      {children}
    </p>
  )
}

function Section({ title, label, children }: { title: string; label: string; children: ReactNode }) {
  const id = useId()
  return (
    <section aria-labelledby={id} className="rounded-lg border border-border p-3">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
        <h3 id={id} className="text-xs font-medium uppercase tracking-wider text-foreground">{title}</h3>
        <p className="text-[11px] text-muted-foreground">{label}</p>
      </div>
      <div className="mt-2 space-y-2">{children}</div>
    </section>
  )
}

function Loading({ what }: { what: string }) {
  return <p className="text-xs text-muted-foreground">Reading {what}…</p>
}

function Failed({ error }: { error: unknown }) {
  return <p role="alert" className="text-xs text-destructive">{describeError(error)}</p>
}

const number = (value: number | null) => (value === null ? 'unknown' : value.toLocaleString('en-US', { maximumFractionDigits: 2 }))

const FLOW_METRICS: Record<string, string> = {
  completed: 'Completed', failed: 'Failed', throughput: 'Throughput', latencyP50: 'Latency p50', latencyP95: 'Latency p95', latencyP99: 'Latency p99',
}

/**
 * Two models side by side: what differs in the model and its contracts, the review findings,
 * the cost estimate, the stress result of the same profile, and Build Ready with the promise
 * results. Each part is what its own endpoint computes. The dialog states differences and
 * gives no verdict and no score. What Vellum does not have is shown as unknown.
 */
export function CompareDialog({ projectId, sides, initial, onClose }: Props) {
  const id = useId()
  const [leftKey, setLeftKey] = useState('main')
  const [rightKey, setRightKey] = useState(() => keyOf(initial.kind === 'main' ? sides.find((s) => s.kind !== 'main') ?? initial : initial))
  const left = sides.find((s) => keyOf(s) === leftKey) ?? sides[0]
  const right = sides.find((s) => keyOf(s) === rightKey) ?? sides[0]
  const same = keyOf(left) === keyOf(right)
  const key = ['projects', projectId, 'compare', keyOf(left), keyOf(right)]
  const fresh = { staleTime: 0, enabled: !same } as const

  const model = useQuery({ queryKey: [...key, 'model'], queryFn: () => branchesApi.compareModels(projectId, left, right), ...fresh })
  const findings = useQuery({
    queryKey: [...key, 'findings'],
    queryFn: () => Promise.all([left, right].map((side) => api<FindingsResponse>('GET', `/api/projects/${projectId}/review/findings${pinOf(side)}`))),
    ...fresh,
  })
  const costable = left.kind !== 'revision' && right.kind !== 'revision'
  const costs = useQuery({
    queryKey: [...key, 'costs'],
    queryFn: () => costsApi.compare(projectId, left.kind === 'main' ? null : sideOf(left), right.kind === 'main' ? null : sideOf(right)),
    ...fresh, enabled: !same && costable,
  })
  const manifests = useQuery({
    queryKey: [...key, 'manifests'],
    queryFn: () => Promise.all([left, right].map((side) => api<BuildManifest>('GET', `/api/projects/${projectId}/build/manifest${pinOf(side)}`))),
    ...fresh,
  })
  const promises = useQuery({ queryKey: ['projects', projectId, 'promises', 'list'], queryFn: () => promisesApi.list(projectId), staleTime: 0 })
  // A stress run names the stream of its model. For each profile, the newest run on each of the two streams is compared.
  const streams = model.data ? [model.data.left.streamId, model.data.right.streamId] : null
  const stress = useQuery({
    queryKey: [...key, 'stress', streams],
    queryFn: async () => {
      const compared: { profile: string; comparison: StressComparison }[] = []
      for (const profile of await stressApi.profiles(projectId)) {
        if (profile.runs === 0) continue
        const runs = await stressApi.runs(projectId, profile.id)
        const newest = (stream: string) => runs.filter((r) => r.modelStreamId === stream).sort((a, b) => b.at.localeCompare(a.at))[0]
        const [a, b] = [newest(streams![0]), newest(streams![1])]
        if (a && b) compared.push({ profile: profile.name, comparison: await stressApi.compare(projectId, a.id, b.id) })
      }
      return compared
    },
    ...fresh, enabled: !same && costable && streams !== null && streams[0] !== streams[1],
  })

  const names = (entityId: string) => {
    for (const change of model.data?.changes ?? []) {
      if (change.entityId !== entityId) continue
      const name = (change.right ?? change.left)?.name
      if (typeof name === 'string') return name
    }
    return undefined
  }
  const changes = model.data?.changes ?? []
  const count = (kind: string) => changes.filter((c) => c.changeKind === kind).length
  const [leftLabel, rightLabel] = [labelOf(left), labelOf(right)]
  const picker = (label: string, value: string, onChange: (value: string) => void) => (
    <label className="block min-w-0 text-[11px] font-medium text-muted-foreground">
      {label}
      <select className={`${selectClass} mt-1`} value={value} onChange={(e) => onChange(e.target.value)}>
        {sides.map((side) => <option key={keyOf(side)} value={keyOf(side)}>{labelOf(side)}</option>)}
      </select>
    </label>
  )
  const heads = (
    <thead className="border-b border-border text-muted-foreground">
      <tr>
        <th className={`${cell} font-medium`} />
        <th className={`${cell} max-w-32 truncate text-right font-medium`} title={leftLabel}>{leftLabel}</th>
        <th className={`${cell} max-w-32 truncate text-right font-medium`} title={rightLabel}>{rightLabel}</th>
      </tr>
    </thead>
  )

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Compare two models</DialogTitle>
          <DialogDescription>
            Each part shows what Vellum computes for the two models. The comparison states differences. It gives no verdict and no score.
          </DialogDescription>
        </DialogHeader>

        <div className="grid grid-cols-2 gap-3">
          {picker('Left', keyOf(left), setLeftKey)}
          {picker('Right', keyOf(right), setRightKey)}
        </div>

        {sides.length < 2 && <p className="text-xs text-muted-foreground">The project has main only. Create a draft, a variant or a revision to compare it with main.</p>}
        {sides.length >= 2 && same && <p id={`${id}-same`} role="status" className="text-xs text-[var(--trace-retry)]">Left and right are the same model. Select two different models.</p>}

        {!same && (
          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1" data-testid="compare-sections">
            <Section title="Model and contracts" label="Each fact carries its own truth label">
              {model.isLoading && <Loading what="the two models" />}
              {model.isError && <Failed error={model.error} />}
              {model.data && (
                <>
                  <p className="text-xs text-muted-foreground" data-testid="compare-model-summary">
                    {changes.length === 0
                      ? 'The two models hold the same facts.'
                      : `From ${leftLabel} to ${rightLabel}: ${count('added')} added · ${count('modified')} modified · ${count('removed')} removed.`}
                    {' '}Left is at version {model.data.left.version}, right at version {model.data.right.version}.
                  </p>
                  {ENTITY_ORDER.filter((type) => changes.some((c) => c.entityType === type)).map((type) => (
                    <div key={type}>
                      <p className="text-[11px] font-medium text-muted-foreground">{ENTITY_LABEL[type]}</p>
                      <ul className="mt-1 space-y-1">
                        {changes.filter((c) => c.entityType === type).map((change) => {
                          const value = change.right ?? change.left
                          return (
                            <li key={change.entityId} className="rounded-md border border-border px-2.5 py-1.5">
                              <details>
                                <summary className="flex cursor-pointer items-center gap-2 text-sm text-foreground focus-visible:outline-2 focus-visible:outline-primary">
                                  <ChangeChip kind={change.changeKind} />
                                  <span className="min-w-0 flex-1 break-words">{entityLabel(type, value, names, change.entityId)}</span>
                                  <span className="shrink-0 text-[11px] text-muted-foreground">{String(value?.factStatus ?? 'unknown')}</span>
                                </summary>
                                <FieldTable left={leftLabel} right={rightLabel} fields={changedFields(change.left, change.right)} empty="No field to show." />
                              </details>
                            </li>
                          )
                        })}
                      </ul>
                    </div>
                  ))}
                </>
              )}
            </Section>

            <Section title="Review findings" label="Computed from each model by the deterministic rules. Not measured.">
              {findings.isLoading && <Loading what="the findings" />}
              {findings.isError && <Failed error={findings.error} />}
              {findings.data && (
                <>
                  <div className="overflow-x-auto rounded-md border border-border">
                    <table className="w-full text-left text-xs">
                      {heads}
                      <tbody>
                        {([
                          ['Blocks Build Ready', 'blocking'], ['Concerns', 'concerns'], ['Unknown', 'unknown'], ['Verified', 'verified'],
                          ['Not applicable', 'notApplicable'], ['Settled by a person', 'settled'],
                        ] as const).map(([name, field]) => (
                          <tr key={field} className="border-b border-border last:border-b-0">
                            <td className={cell}>{name}</td>
                            <td className={numeric}>{findings.data[0].counts[field]}</td>
                            <td className={numeric}>{findings.data[1].counts[field]}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    {findings.data.map((side, i) => {
                      const blockers = side.findings.filter((f) => f.blocks)
                      return (
                        <div key={i} className="min-w-0">
                          <p className="truncate text-[11px] font-medium text-muted-foreground">Blockers in {i === 0 ? leftLabel : rightLabel}</p>
                          {blockers.length === 0 && <p className="text-xs text-foreground">No finding blocks Build Ready.</p>}
                          <ul className="mt-0.5 space-y-0.5">
                            {blockers.slice(0, 6).map((f) => (
                              <li key={f.finding.ruleId + f.finding.subject.id} className="truncate text-xs text-foreground" title={f.finding.reason}>
                                <span className="font-mono text-muted-foreground">{f.finding.ruleId}</span> {f.finding.subject.name}
                              </li>
                            ))}
                          </ul>
                          {blockers.length > 6 && <p className="text-[11px] text-muted-foreground">and {blockers.length - 6} more. Open Review on that model for the full list.</p>}
                        </div>
                      )
                    })}
                  </div>
                </>
              )}
            </Section>

            <Section title="Cost for one month" label="Estimated from the cost sheets and the price book. Not measured.">
              {!costable && <Unknown>A revision holds no cost sheet. Create a variant from the revision to estimate its cost.</Unknown>}
              {costs.isLoading && <Loading what="the two estimates" />}
              {costs.isError && <Failed error={costs.error} />}
              {costs.data && (
                <>
                  {!costs.data.comparison.comparable && (
                    <p className="text-xs text-[var(--trace-retry)]" data-testid="compare-cost-reason">
                      Not comparable. {NOT_COMPARABLE[costs.data.comparison.reason!] ?? costs.data.comparison.reason} The totals below hold known lines only.
                    </p>
                  )}
                  {costs.data.comparison.totals.length === 0
                    ? <Unknown>Neither side has a line with a known cost.</Unknown>
                    : (
                      <div className="overflow-x-auto rounded-md border border-border">
                        <table className="w-full text-left text-xs" data-testid="compare-cost-totals">
                          <thead className="border-b border-border text-muted-foreground">
                            <tr>
                              <th className={`${cell} font-medium`}>Total, known lines</th>
                              <th className={`${cell} max-w-32 truncate text-right font-medium`} title={leftLabel}>{leftLabel}</th>
                              <th className={`${cell} max-w-32 truncate text-right font-medium`} title={rightLabel}>{rightLabel}</th>
                              <th className={`${cell} text-right font-medium`}>Right minus left</th>
                            </tr>
                          </thead>
                          <tbody>
                            {costs.data.comparison.totals.map((total) => (
                              <tr key={total.currency} className="border-b border-border last:border-b-0">
                                <td className={cell}>{total.currency}</td>
                                <td className={numeric}>{total.left === null ? 'unknown' : formatAmount(total.left)}</td>
                                <td className={numeric}>{total.right === null ? 'unknown' : formatAmount(total.right)}</td>
                                <td className={numeric}>{total.delta === null ? 'unknown' : `${total.delta > 0 ? '+' : ''}${formatAmount(total.delta)}`}</td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  <p className="text-[11px] text-muted-foreground">
                    Lines with no known cost: {costs.data.comparison.leftUnknown.length} in {leftLabel}, {costs.data.comparison.rightUnknown.length} in {rightLabel}. They are in no total. Open Costs for the lines.
                  </p>
                </>
              )}
            </Section>

            <Section title="Stress result of the same profile" label="Estimated by the seeded simulation. Not measured.">
              {!costable && <Unknown>A stress run names a branch. A revision has no run. Create a variant from the revision and run a profile on it.</Unknown>}
              {costable && (model.isLoading || stress.isLoading) && <Loading what="the stress runs" />}
              {stress.isError && <Failed error={stress.error} />}
              {stress.data?.length === 0 && <Unknown>No stress profile has a run on both models. Open Stress and run the same profile on each model.</Unknown>}
              {stress.data?.map(({ profile, comparison }) => (
                <div key={profile}>
                  <p className="text-xs font-medium text-foreground">{profile} <span className="font-normal text-muted-foreground">· {comparison.label} · newest run on each model</span></p>
                  {(comparison.left.outdated || comparison.right.outdated) && (
                    <p className="text-[11px] text-[var(--trace-retry)]">
                      {[comparison.left.outdated ? leftLabel : null, comparison.right.outdated ? rightLabel : null].filter(Boolean).join(' and ')} changed after the run. The run is of an older state.
                    </p>
                  )}
                  <div className="mt-1 overflow-x-auto rounded-md border border-border">
                    <table className="w-full text-left text-xs">
                      {heads}
                      <tbody>
                        {comparison.metrics.filter((m) => m.scope === 'flow' && FLOW_METRICS[m.metric]).map((m) => (
                          <tr key={m.metric} className="border-b border-border last:border-b-0">
                            <td className={cell}>{FLOW_METRICS[m.metric]} <span className="text-muted-foreground">{m.unit}</span></td>
                            <td className={numeric}>{number(m.left)}</td>
                            <td className={numeric}>{number(m.right)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <p className="mt-1 text-[11px] text-muted-foreground">{comparison.inputs.length} {comparison.inputs.length === 1 ? 'input differs' : 'inputs differ'} between the two runs. {comparison.note}</p>
                </div>
              ))}
            </Section>

            <Section title="Build Ready and promises" label="From recorded evidence. A result with no evidence is not a pass.">
              {manifests.isLoading && <Loading what="the build state" />}
              {manifests.isError && <Failed error={manifests.error} />}
              {manifests.data && (
                <div className="grid gap-2 sm:grid-cols-2">
                  {manifests.data.map((manifest, i) => (
                    <div key={i} className="min-w-0 rounded-md border border-border px-2.5 py-2">
                      <p className="truncate text-[11px] font-medium text-muted-foreground">{i === 0 ? leftLabel : rightLabel}</p>
                      <p className="text-sm text-foreground">
                        {manifest.buildReady ? 'Build ready' : `Not build ready: ${manifest.blockers.length} ${manifest.blockers.length === 1 ? 'blocker' : 'blockers'}`}
                      </p>
                      <ul className="mt-0.5 space-y-0.5">
                        {manifest.blockers.slice(0, 4).map((blocker, n) => (
                          <li key={n} className="truncate text-xs text-muted-foreground" title={blocker.message}>
                            <span className="font-mono">{blocker.code}</span> {blocker.subject}
                          </li>
                        ))}
                      </ul>
                      {manifest.blockers.length > 4 && <p className="text-[11px] text-muted-foreground">and {manifest.blockers.length - 4} more</p>}
                    </div>
                  ))}
                </div>
              )}
              {promises.isError && <Failed error={promises.error} />}
              {promises.data && (promises.data.length === 0
                ? <Unknown>The project has no promise, so no verification result exists for either model.</Unknown>
                : (
                  <p className="text-xs text-foreground" data-testid="compare-promises">
                    Promises belong to the project, not to one model, so both sides share them: {promises.data.filter((p) => p.state === 'approved').length} approved,{' '}
                    {promises.data.filter((p) => p.state === 'draft').length} draft. Obligations:{' '}
                    {(['passed', 'failed', 'notRun', 'stale', 'unsupported'] as const)
                      .map((field) => `${promises.data.reduce((n, p) => n + p.counts[field], 0)} ${field === 'notRun' ? 'not run' : field}`).join(' · ')}.
                  </p>
                ))}
            </Section>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
