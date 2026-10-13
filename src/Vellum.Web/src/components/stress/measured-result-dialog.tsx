import { useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  loadTestsApi, measurementRef, METRIC_NAMES,
  type EstimateComparison, type MeasuredLatency, type MeasuredResult, type MeasuredResultSummary, type Regression, type Verdict,
} from '@/lib/load-tests-api'
import { formatMs, formatNumber, formatPercent, stressApi } from '@/lib/stress-api'
import { EstimatedLabel, EvidenceTag, MeasuredLabel, UnknownLabel, estimatedCell, measuredCell } from './evidence-labels'

interface Props {
  projectId: string
  result: MeasuredResult
  /** Every measured result of the project, for the choice of a baseline. */
  results: MeasuredResultSummary[]
  profileName: string | null
  onClose: () => void
}

const selectClass =
  'h-8 max-w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30'
const th = 'px-2 py-1 text-right font-medium first:text-left'
const td = 'whitespace-nowrap px-2 py-1 text-right font-mono first:whitespace-normal first:text-left first:font-sans'

const value = (number: number | null, unit: string) => (unit === 'ms' ? formatMs(number) : unit === 'of 1' ? formatPercent(number) : formatNumber(number, 2))
const signed = (number: number | null, unit: string) => {
  if (number === null) return null
  const sign = number > 0 ? '+' : ''
  return unit === 'ms' ? `${sign}${formatMs(number)}` : unit === 'of 1' ? `${sign}${(number * 100).toFixed(2)} pt` : `${sign}${formatNumber(number, 2)}`
}

function Section({ id, title, tags, note, children }: { id: string; title: string; tags?: ReactNode; note?: string; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="space-y-1.5">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={id} className="text-xs font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
        {tags}
      </div>
      {note && <p className="text-[11px] leading-relaxed text-muted-foreground">{note}</p>}
      {children}
    </section>
  )
}

function Tile({ label, value: text, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div className="rounded-md border border-l-2 border-border border-l-[var(--trace-request)] px-3 py-2">
      <p className="flex items-center justify-between gap-2 text-[11px] text-muted-foreground">{label}<MeasuredLabel /></p>
      <p className="mt-0.5 font-mono text-lg font-semibold text-foreground">{text}</p>
      {detail && <p className="text-[11px] text-muted-foreground">{detail}</p>}
    </div>
  )
}

function LatencyCells({ latency }: { latency: MeasuredLatency | null }) {
  if (!latency) return <td className={td} colSpan={5}><UnknownLabel /> <span className="font-sans text-[11px] text-muted-foreground">no request got the expected status</span></td>
  return <>{[latency.p50Ms, latency.p90Ms, latency.p95Ms, latency.p99Ms, latency.maxMs].map((ms, i) => <td key={i} className={`${td} text-foreground`}>{formatMs(ms)}</td>)}</>
}

function Notes({ notes }: { notes: string[] }) {
  if (notes.length === 0) return null
  return <ul className="list-disc space-y-0.5 pl-4 text-[11px] leading-relaxed text-[var(--trace-retry)]">{notes.map((note, i) => <li key={i}>{note}</li>)}</ul>
}

function EstimateTable({ comparison }: { comparison: EstimateComparison }) {
  return (
    <div className="space-y-2">
      <Notes notes={comparison.differences} />
      <div className="max-h-96 overflow-auto rounded-md border border-border">
        <table className="w-full border-collapse text-xs text-muted-foreground">
          <thead className="sticky top-0 bg-popover">
            <tr>
              <th scope="col" className={th}>Metric</th>
              <th scope="col" className={th}><span className="inline-flex items-center gap-1.5">From the simulation <EstimatedLabel /></span></th>
              <th scope="col" className={th}><span className="inline-flex items-center gap-1.5">From the k6 run <MeasuredLabel /></span></th>
              <th scope="col" className={th}>Measured minus estimated</th>
            </tr>
          </thead>
          <tbody>
            {comparison.rows.map((row, i) => (
              <tr key={i} className="border-t border-border">
                <th scope="row" className={`${td} font-normal text-foreground`}>{row.scope === 'flow' ? 'Whole flow' : row.name} · {METRIC_NAMES[row.metric] ?? row.metric}</th>
                <td className={`${td} ${row.estimated === null ? '' : estimatedCell}`}>
                  {row.estimated === null ? <EvidenceTag label="unknown" /> : <span title="Estimated">~ {value(row.estimated, row.unit)}</span>}
                </td>
                <td className={`${td} ${row.measured === null ? '' : measuredCell}`}>
                  {row.measured === null ? <EvidenceTag label="unknown" /> : <span title="Measured">{value(row.measured, row.unit)}</span>}
                </td>
                <td className={`${td} text-foreground`}>{signed(row.difference, row.unit) ?? <span className="font-sans text-[11px] text-muted-foreground">no difference to state</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-[11px] leading-relaxed text-muted-foreground">{comparison.note} An estimate is written with “~”. “Unknown” is a missing value: it is not zero and it is not a match.</p>
    </div>
  )
}

const VERDICT: Record<Verdict, { text: string; className: string }> = {
  regressed: { text: 'Regressed', className: 'bg-[var(--trace-failure)] text-[hsl(0_30%_8%)]' },
  improved: { text: 'Improved', className: 'border border-[var(--trace-request)] text-[var(--trace-request)]' },
  unchanged: { text: 'Unchanged', className: 'border border-border text-muted-foreground' },
  unknown: { text: 'Unknown', className: 'border border-dashed border-muted-foreground/70 text-muted-foreground' },
}

function RegressionTable({ regression }: { regression: Regression }) {
  const regressed = regression.subjects.filter((s) => s.regressed)
  return (
    <div className="space-y-2">
      <p role="status" className={`rounded-md border px-3 py-2 text-xs text-foreground ${regressed.length > 0 ? 'border-[var(--trace-failure)]/60' : 'border-border'}`}>
        {regressed.length === 0
          ? 'No regression: no row of this run is worse than the baseline by more than the tolerance.'
          : `Regressed: ${regressed.map((s) => `${s.scope === 'flow' ? 'the whole flow' : s.name} (${s.regressedMetrics.map((m) => METRIC_NAMES[m] ?? m).join(', ')})`).join('; ')}.`}
      </p>
      <Notes notes={regression.differences} />
      <div className="max-h-96 overflow-auto rounded-md border border-border">
        <table className="w-full border-collapse text-xs text-muted-foreground">
          <thead className="sticky top-0 bg-popover">
            <tr>
              <th scope="col" className={th}>Metric</th>
              <th scope="col" className={th}>Baseline</th>
              <th scope="col" className={th}>This run</th>
              <th scope="col" className={th}>Change</th>
              <th scope="col" className={th}>Verdict</th>
            </tr>
          </thead>
          <tbody>
            {regression.rows.map((row, i) => (
              <tr key={i} className="border-t border-border">
                <th scope="row" className={`${td} font-normal text-foreground`}>{row.scope === 'flow' ? 'Whole flow' : row.name} · {METRIC_NAMES[row.metric] ?? row.metric}</th>
                <td className={td}>{row.baseline === null ? <UnknownLabel /> : value(row.baseline, row.unit)}</td>
                <td className={`${td} text-foreground`}>{row.candidate === null ? <UnknownLabel /> : value(row.candidate, row.unit)}</td>
                <td className={td}>
                  {signed(row.change, row.unit) ?? '–'}
                  {row.relative !== null && <span className="ml-1.5 text-[11px]">({row.relative > 0 ? '+' : ''}{(row.relative * 100).toFixed(1)} %)</span>}
                </td>
                <td className={td}><span className={`rounded-sm px-1.5 py-px font-sans text-[10px] font-medium uppercase tracking-wider ${VERDICT[row.verdict].className}`}>{VERDICT[row.verdict].text}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="text-[11px] leading-relaxed text-muted-foreground">{regression.rule}</p>
    </div>
  )
}

/**
 * One imported k6 result. Every number here was read from the imported file, and the view
 * says so with the Measured tag. An estimate from the simulation appears only in the
 * comparison, in its own column, with its own tag, colour and rule.
 */
export function MeasuredResultDialog({ projectId, result, results, profileName, onClose }: Props) {
  const { result: pin, operations, notCovered } = result
  const provenance = pin.provenance
  const [runId, setRunId] = useState('')
  const [baselineId, setBaselineId] = useState('')

  const runs = useQuery({
    queryKey: ['projects', projectId, 'stress', 'runs', pin.profileId],
    queryFn: () => stressApi.runs(projectId, pin.profileId),
    retry: false,
  })
  const estimate = useQuery({
    queryKey: ['projects', projectId, 'load-tests', 'estimate', pin.id, runId],
    queryFn: () => loadTestsApi.compareEstimate(projectId, pin.id, runId),
    enabled: runId !== '',
  })
  const regression = useQuery({
    queryKey: ['projects', projectId, 'load-tests', 'regression', baselineId, pin.id],
    queryFn: () => loadTestsApi.compareResults(projectId, baselineId, pin.id),
    enabled: baselineId !== '',
  })
  // A baseline is another run of the same scenario against the same target.
  const baselines = results.filter((r) => r.id !== pin.id && r.scenarioId === pin.scenarioId && r.provenance.targetId === provenance.targetId)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[94vh] flex-col gap-3 sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle className="flex flex-wrap items-center gap-2">k6 run against {provenance.targetName} <MeasuredLabel /></DialogTitle>
          <DialogDescription>
            Numbers that k6 measured on a deployed system, read from the file that was imported. They hold for this run, this load and this target.
            <span className="mt-1 block font-mono text-[11px]">
              {new Date(pin.startedAt).toLocaleString()} · {formatNumber(pin.measuredSeconds, 1)} s measured · {provenance.baseUrl} ({provenance.environment}) · script {provenance.scriptHash.slice(0, 12)}
              {' '}· scenario version {pin.scenarioVersion} · model version {pin.modelVersion}{profileName ? ` · ${profileName} v${pin.profileVersion}` : ''}
            </span>
            <span className="mt-0.5 block text-[11px]">
              Imported {new Date(provenance.importedAt).toLocaleString()} by user {provenance.importedBy}{provenance.importedVia ? ` through ${provenance.importedVia}` : ''} · {provenance.format}
            </span>
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          {pin.stale && (
            <p role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
              Stale: the {pin.staleBecause.join(' and the ')} changed after the script of this run was generated. The numbers are what k6 measured then.
              Generate a new script and run it to measure the flow as it is declared now.
            </p>
          )}

          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
            <Tile label="Throughput" value={`${formatNumber(pin.flow.throughputPerSecond, 2)} /s`} detail={`${formatNumber(pin.flow.iterations)} flows after the warm-up`} />
            <Tile label="Latency, p50" value={formatMs(pin.flow.latency?.p50Ms)} detail={`mean ${formatMs(pin.flow.latency?.meanMs)}`} />
            <Tile label="Latency, p95" value={formatMs(pin.flow.latency?.p95Ms)} detail={`p99 ${formatMs(pin.flow.latency?.p99Ms)}`} />
            <Tile label="Failed flows" value={formatPercent(pin.flow.errorRate)} detail={`${formatNumber(pin.flow.failed)} failed · ${formatNumber(pin.flow.droppedIterations)} iterations dropped by k6`} />
          </div>

          <Section id="measured-operations" title="Operations" tags={<MeasuredLabel />} note="Latency is the duration of the requests that got the expected status, as k6 saw it. The percentiles are the ones k6 computed.">
            <div className="overflow-x-auto rounded-md border border-border">
              <table className="w-full border-collapse text-xs text-muted-foreground">
                <thead>
                  <tr>{['Operation', 'Requests', 'Failed', 'Error rate', 'Per second', 'p50', 'p90', 'p95', 'p99', 'Max'].map((h, i) => <th key={i} scope="col" className={th}>{h}</th>)}</tr>
                </thead>
                <tbody>
                  {operations.map((o) => (
                    <tr key={o.operationId} className="border-t border-border">
                      <th scope="row" className={`${td} font-normal text-foreground`}>{o.name}</th>
                      <td className={td}>{formatNumber(o.requests)}</td>
                      <td className={td}>{formatNumber(o.failed)}</td>
                      <td className={td}>{o.errorRate === null ? <UnknownLabel /> : formatPercent(o.errorRate)}</td>
                      <td className={td}>{o.requests === 0 ? <UnknownLabel /> : formatNumber(o.throughputPerSecond, 2)}</td>
                      <LatencyCells latency={o.latency} />
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Section>

          <Section id="measured-not-covered" title="Not covered by this run" note="The script did not perform these steps. This result says nothing about them.">
            {notCovered.length === 0 ? (
              <p className="text-xs text-foreground">Nothing. The script drove every step of the scenario.</p>
            ) : (
              <ul className="grid gap-x-6 gap-y-1 text-xs text-foreground md:grid-cols-2">
                {notCovered.map((skipped) => (
                  <li key={skipped.stepId}>
                    <span className="font-mono">{skipped.stepId}</span> <span className="text-[11px] text-muted-foreground">({skipped.kind})</span>
                    <span className="block text-[11px] text-muted-foreground">{skipped.reason}</span>
                  </li>
                ))}
              </ul>
            )}
          </Section>

          <Section id="measured-estimate" title="Estimated versus measured" tags={<><EstimatedLabel /><MeasuredLabel /></>}>
            {runs.data && runs.data.length === 0 && (
              <p className="text-xs text-muted-foreground">The stress profile of this script has no stored simulation run. Run the profile under Simulate, then compare here.</p>
            )}
            {runs.isError && <p className="text-xs text-muted-foreground">The stress profile of this script does not exist any more, so there is no simulation run to compare with.</p>}
            {runs.data && runs.data.length > 0 && (
              <label className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                Simulation run
                <select className={selectClass} value={runId} onChange={(e) => setRunId(e.target.value)}>
                  <option value="">Choose a run</option>
                  {runs.data.map((r) => (
                    <option key={r.id} value={r.id}>{new Date(r.at).toLocaleString()} · profile v{r.profileVersion} · seed {r.seed}{r.outdated ? ' · outdated' : ''}</option>
                  ))}
                </select>
              </label>
            )}
            {estimate.isError && <p role="alert" className="text-xs text-destructive">{estimate.error.message}</p>}
            {estimate.data && runId !== '' && <EstimateTable comparison={estimate.data} />}
          </Section>

          <Section id="measured-regression" title="Compare with a baseline run" tags={<MeasuredLabel />}>
            {baselines.length === 0 ? (
              <p className="text-xs text-muted-foreground">No other measured result of this scenario and this target. Import the result of another run to compare the two.</p>
            ) : (
              <label className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                Baseline
                <select className={selectClass} value={baselineId} onChange={(e) => setBaselineId(e.target.value)}>
                  <option value="">Choose a measured result</option>
                  {baselines.map((r) => (
                    <option key={r.id} value={r.id}>{new Date(r.startedAt).toLocaleString()} · p95 {formatMs(r.flow.latency?.p95Ms)}{r.stale ? ' · stale' : ''}</option>
                  ))}
                </select>
              </label>
            )}
            {regression.isError && <p role="alert" className="text-xs text-destructive">{regression.error.message}</p>}
            {regression.data && baselineId !== '' && <RegressionTable regression={regression.data} />}
          </Section>

          <Section id="measured-use" title="Use in a stress profile" note="A value of a stress profile takes the label “measured” only when it names an imported result. This is the name of this result:">
            <p className="font-mono text-xs text-foreground">{measurementRef(pin.id)}</p>
          </Section>
        </div>
      </DialogContent>
    </Dialog>
  )
}
