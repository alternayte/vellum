import { useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  formatMs, formatNumber, formatPercent, stressApi,
  type LatencySummary, type ResourceStats, type StressComparison, type StressResult, type StressRun, type StressRunSummary,
} from '@/lib/stress-api'
import { LineChart, type ChartSeries } from './line-chart'
import { Layers } from 'lucide-react'
import { Button } from '@/components/ui/button'

interface Props {
  projectId: string
  profileName: string
  run: StressRun
  /** The stored runs of the same profile, newest first, for the comparison. */
  runs: StressRunSummary[]
  onClose: () => void
  /** Draws this run on the canvas. Absent where the canvas is not beside the dialog. */
  onShowOnCanvas?: () => void
}

/** The six series colours, in a fixed order. A resource keeps its colour whatever else is shown. */
const SERIES = ['var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)', 'var(--chart-6)']
const MAX_DEPTH_SERIES = SERIES.length

const selectClass =
  'h-8 rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30'

function Estimated() {
  return <span className="rounded-full border border-[var(--trace-retry)] px-1.5 py-px text-[10px] font-medium uppercase tracking-wider text-[var(--trace-retry)]">Estimated</span>
}

function Section({ id, title, estimated = true, note, rule, children }: { id: string; title: string; estimated?: boolean; note?: string; rule?: string; children: ReactNode }) {
  return (
    <section aria-labelledby={id} className="space-y-1.5">
      <div className="flex flex-wrap items-center gap-2">
        <h3 id={id} className="text-xs font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
        {estimated && <Estimated />}
      </div>
      {note && <p className="text-[11px] leading-relaxed text-muted-foreground">{note}</p>}
      {rule && <RuleNote text={rule} />}
      {children}
    </section>
  )
}

/**
 * A long engine rule: the first sentence says what the rule decides; the full text, with its
 * formulas, opens on request.
 */
function RuleNote({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  const [first, ...rest] = text.split(/(?<=\.)\s+(?=[A-Z])/)
  return (
    <div className="text-[11px] leading-relaxed text-muted-foreground">
      <p>
        {first}
        {rest.length > 0 && (
          <>
            {' '}
            <button
              type="button"
              aria-expanded={open}
              onClick={() => setOpen(!open)}
              className="rounded text-foreground underline underline-offset-2 hover:text-primary focus-visible:outline-2 focus-visible:outline-primary"
            >
              {open ? 'Hide the full rule' : 'Show the full rule'}
            </button>
          </>
        )}
      </p>
      {open && <p className="mt-1 max-w-prose">{rest.join(' ')}</p>}
    </div>
  )
}

function Tile({ label, value, detail }: { label: string; value: string; detail?: string }) {
  return (
    <div className="rounded-md border border-border px-3 py-2">
      <p className="flex items-center justify-between gap-2 text-[11px] text-muted-foreground">{label}<Estimated /></p>
      <p className="mt-0.5 font-mono text-lg text-foreground">{value}</p>
      {detail && <p className="text-[11px] text-muted-foreground">{detail}</p>}
    </div>
  )
}

const th = 'px-2 py-1 text-right font-medium first:text-left'
const td = 'whitespace-nowrap px-2 py-1 text-right font-mono first:whitespace-normal first:text-left first:font-sans'

function LatencyRow({ name, kind, latency }: { name: string; kind: string; latency: LatencySummary }) {
  return (
    <tr className="border-t border-border">
      <th scope="row" className={`${td} font-normal text-foreground`}>{name}<span className="ml-1.5 text-[10px] text-muted-foreground">{kind}</span></th>
      <td className={td}>{formatNumber(latency.count)}{latency.reservoir ? '*' : ''}</td>
      {[latency.minMs, latency.meanMs, latency.p50Ms, latency.p90Ms, latency.p95Ms, latency.p99Ms, latency.maxMs].map((value, i) => (
        <td key={i} className={`${td} text-foreground`}>{formatMs(value)}</td>
      ))}
    </tr>
  )
}

const delta = (value: number | null, unit: string) => {
  if (value === null) return '–'
  const sign = value > 0 ? '+' : ''
  return unit === 'ms' ? `${sign}${formatMs(value)}` : unit === 'of 1' ? `${sign}${(value * 100).toFixed(1)} pt` : `${sign}${formatNumber(value, 2)}`
}
const side = (value: number | null, unit: string) => (unit === 'ms' ? formatMs(value) : unit === 'of 1' ? formatPercent(value) : formatNumber(value, 2))

const METRIC_NAMES: Record<string, string> = {
  arrivals: 'Arrivals', completed: 'Completed', failed: 'Failed', rejected: 'Rejected', timedOut: 'Timed out', dropped: 'Dropped',
  deadLettered: 'Dead-lettered', throughput: 'Throughput, per second', latencyMean: 'Latency, mean', latencyP50: 'Latency, p50', latencyP90: 'Latency, p90',
  latencyP95: 'Latency, p95', latencyP99: 'Latency, p99', latencyMax: 'Latency, max', timeToDrain: 'Time to drain', utilisation: 'Utilisation',
  meanQueueDepth: 'Mean queue depth', waitMean: 'Wait, mean', retries: 'Retries',
  wasted: 'Wasted calls', failedInside: 'Calls that failed inside',
}

/** Virtual time of a run, in seconds. */
const at = (ms: number) => `${formatNumber(ms / 1000, 1)} s`

/**
 * What the rules of the engine found, and nothing else. A resource is named when the
 * saturation rule names it. Whether its queue grows is the outcome of another rule, so it
 * is a sentence of its own.
 */
function BottleneckStatement({ result, resources }: { result: StressResult; resources: ResourceStats[] }) {
  const bottleneck = result.bottleneck
  const box = 'rounded-md border px-3 py-2 text-sm text-foreground'

  // A result of engine version 1. Its rule is not the rule of this page, so its outcome is shown as what that engine stored.
  if (bottleneck.saturated === null) {
    return (
      <p className={`${box} border-border`}>
        {bottleneck.found
          ? <><span className="font-medium">{bottleneck.name}</span> ({bottleneck.kind}) is the resource that engine version {result.engineVersion} named: utilisation {formatPercent(bottleneck.utilisation)}.</>
          : <>Engine version {result.engineVersion} named no resource.</>}
        <span className="mt-0.5 block text-xs text-muted-foreground">
          {bottleneck.found && <>Mean queue depth by quarter of the measured window: {bottleneck.meanDepthByQuarter?.map((q) => formatNumber(q, 1)).join(' → ')}. Depth when the arrivals stop: {formatNumber(bottleneck.depthAtEndOfArrivals)}. </>}
          The rule of that version looked for a queue whose mean depth rises in every quarter. It did not read rejected work, and it did not read a backlog that shrinks again. Run the profile again to apply the current rule.
        </span>
      </p>
    )
  }

  const named = bottleneck.saturated.map((id) => resources.find((r) => r.id === id)).filter((r): r is ResourceStats => !!r?.saturation)
  if (named.length === 0) {
    const deepest = resources.reduce<ResourceStats | null>((best, r) => (r.queue.maxDepth > (best?.queue.maxDepth ?? 0) ? r : best), null)
    return (
      <p className={`${box} border-border`}>
        No resource is saturated by the rule. No work was turned away after the warm-up, and no queue reached its noise bound in the measured window.
        {deepest?.saturation && (
          <span className="mt-0.5 block text-xs text-muted-foreground">
            The deepest queue is {formatNumber(deepest.queue.maxDepth)} at {deepest.name}. Its noise bound is {formatNumber(deepest.saturation.noiseBound)}.
          </span>
        )}
      </p>
    )
  }

  return (
    <ul className="space-y-2">
      {named.map((r, i) => {
        const s = r.saturation!
        return (
          <li key={r.id} className={`${box} ${i === 0 ? 'border-[var(--trace-retry)]/60' : 'border-border'}`}>
            <span className="font-medium">{r.name}</span> ({r.kind}) is saturated{i === 0 ? ' and is the bottleneck' : ''}: utilisation {formatPercent(r.utilisation)}.
            <span className="mt-0.5 block space-y-0.5 text-xs text-muted-foreground">
              {s.turnedAway > 0 && (
                <span className="block">
                  It turned away {formatNumber(s.turnedAway)} {s.turnedAway === 1 ? 'piece' : 'pieces'} of work because its queue was full, from {at(s.firstTurnedAwayMs ?? 0)} to {at(s.lastTurnedAwayMs ?? 0)}.
                </span>
              )}
              {s.windows.map((w, k) => (
                <span key={k} className="block">
                  Work waited without a break from {at(w.fromMs)} to {w.toMs === null ? 'the end of the run' : at(w.toMs)}. The queue reached {formatNumber(w.peakDepth)} at {at(w.peakAtMs)}; the noise bound is {formatNumber(s.noiseBound)}.
                </span>
              ))}
              {s.moreWindows && <span className="block">The list holds the {s.windows.length} windows with the deepest queue. The run can have more.</span>}
              <span className="block">
                {s.queueGrows
                  ? <>Its queue grows by about {formatNumber(s.growthPerSecond, 2)} each second at the end of the arrivals: </>
                  : <>Its queue does not grow at the end of the arrivals: </>}
                {formatNumber(s.depthAtMidWindow)} wait at the middle of the measured window and {formatNumber(r.queue.depthAtEndOfArrivals)} when the arrivals stop.
              </span>
            </span>
          </li>
        )
      })}
    </ul>
  )
}

function Comparison({ comparison }: { comparison: StressComparison }) {
  const when = (run: StressRunSummary) => `${new Date(run.at).toLocaleString()} · profile v${run.profileVersion} · seed ${run.seed}`
  return (
    <div className="space-y-3">
      <p className="text-[11px] leading-relaxed text-muted-foreground">
        Left: {when(comparison.left)}. Right: {when(comparison.right)}. {comparison.note}
      </p>
      <div>
        <p className="text-xs font-medium text-foreground">Inputs that differ</p>
        <p className="mt-1 text-xs text-muted-foreground">
          {comparison.sameResult
            ? 'The result hashes are equal. The two runs have the same result.'
            : 'The result hashes differ. The two runs do not have the same result.'}
          {comparison.inputs.length === 0 && ' No listed input differs.'}
        </p>
        {comparison.inputs.length > 0 && (
            <div className="mt-1 overflow-x-auto rounded-md border border-border">
              <table className="w-full border-collapse text-xs text-muted-foreground">
                <thead><tr><th scope="col" className={th}>Input</th><th scope="col" className={th}>Left</th><th scope="col" className={th}>Right</th></tr></thead>
                <tbody>
                  {comparison.inputs.map((input, i) => (
                    <tr key={i} className="border-t border-border">
                      <th scope="row" className={`${td} font-normal text-foreground`}>{input.name === 'Run' ? '' : `${input.name} · `}<span className="font-mono text-[11px]">{input.input}</span></th>
                      <td className={td}>{input.left ?? 'not set'}</td>
                      <td className={`${td} text-foreground`}>{input.right ?? 'not set'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </div>
      <div>
        <p className="flex items-center gap-2 text-xs font-medium text-foreground">Metric differences <Estimated /></p>
        <div className="mt-1 max-h-80 overflow-auto rounded-md border border-border">
          <table className="w-full border-collapse text-xs text-muted-foreground">
            <thead className="sticky top-0 bg-popover">
              <tr><th scope="col" className={th}>Metric</th><th scope="col" className={th}>Left</th><th scope="col" className={th}>Right</th><th scope="col" className={th}>Right minus left</th></tr>
            </thead>
            <tbody>
              {comparison.metrics.map((metric, i) => (
                <tr key={i} className="border-t border-border">
                  <th scope="row" className={`${td} font-normal text-foreground`}>{metric.scope === 'flow' ? '' : `${metric.name} · `}{METRIC_NAMES[metric.metric] ?? metric.metric}</th>
                  <td className={td}>{side(metric.left, metric.unit)}</td>
                  <td className={td}>{side(metric.right, metric.unit)}</td>
                  <td className={`${td} text-foreground`}>{delta(metric.delta, metric.unit)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  )
}

/**
 * One stored run. Every number is an estimate from a model of the declared flow, and the
 * view says so next to each group of numbers. What the model leaves out is always on screen.
 */
export function StressResultDialog({ projectId, profileName, run, runs, onClose, onShowOnCanvas }: Props) {
  const { result, run: pin } = run
  const [other, setOther] = useState('')
  const comparison = useQuery({
    queryKey: ['projects', projectId, 'stress', 'compare', other, pin.id],
    queryFn: () => stressApi.compare(projectId, other, pin.id),
    enabled: other !== '',
  })

  const resources: ResourceStats[] = [...result.elements, ...result.subscriptions]
  const series = result.series
  const marks = [
    { atMs: result.window.warmUpMs, label: 'warm-up ends' },
    { atMs: result.window.durationMs, label: 'arrivals stop' },
  ]
  const rates: ChartSeries[] = [
    { name: 'Arrivals', color: SERIES[0], values: series.arrivalsPerSecond },
    { name: 'Completed', color: SERIES[2], values: series.throughputPerSecond },
    { name: 'Failed', color: SERIES[1], values: series.failuresPerSecond },
  ]
  // Only the deepest queues are drawn when there are more than the colour order holds. They keep the order of the result.
  const depths = series.resources
    .map((r, i) => ({ r, i, peak: Math.max(0, ...r.queueDepth) }))
    .sort((a, b) => b.peak - a.peak || a.i - b.i)
    .slice(0, MAX_DEPTH_SERIES)
    .sort((a, b) => a.i - b.i)
    .map(({ r }, shownAt) => ({ name: r.name, color: SERIES[shownAt], values: r.queueDepth }))
  const hidden = series.resources.length - depths.length
  const others = runs.filter((r) => r.id !== pin.id)
  const bottleneck = result.bottleneck
  // A result of engine version 1 has no saturation data and counts completed work in another way.
  const olderEngine = bottleneck.saturated === null

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[94vh] flex-col gap-3 sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle className="flex flex-wrap items-center gap-2">
            {profileName} <Estimated />
            {onShowOnCanvas && (
              <Button variant="outline" size="sm" className="ml-auto mr-6 h-7 gap-1.5 text-xs" onClick={onShowOnCanvas}>
                <Layers className="h-3.5 w-3.5" aria-hidden="true" /> Show on the canvas
              </Button>
            )}
          </DialogTitle>
          <DialogDescription>
            A simulation of the declared flow under the declared load. It is not a measurement of a running system.
            <span className="mt-1 block font-mono text-[11px]">
              profile v{pin.profileVersion} · model version {pin.modelVersion} · scenario version {pin.scenarioVersion} · seed {pin.seed} · engine {pin.engineVersion} · {pin.hash.slice(0, 12)}
            </span>
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          {pin.summary.failedElement && (
            <p role="status" className="rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs text-foreground">
              {pin.summary.failedElement} is down for the whole run: an outage on top of the profile. The profile itself does not have it.
            </p>
          )}
          {(pin.outdated || pin.profileVersion !== pin.currentProfileVersion) && (
            <p role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
              {pin.outdated && <>Outdated: the {pin.outdatedBecause.join(' and the ')} changed after this run. </>}
              {pin.profileVersion !== pin.currentProfileVersion && <>The profile is now at version {pin.currentProfileVersion}. </>}
              Run the profile again to get a result for the current inputs.
            </p>
          )}

          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
            <Tile label="Throughput" value={`${formatNumber(result.flow.throughputPerSecond, 2)} /s`} detail={`${formatNumber(result.flow.arrivals)} arrivals after the warm-up`} />
            <Tile label="Latency, p50" value={formatMs(result.flow.latency.p50Ms)} detail={`mean ${formatMs(result.flow.latency.meanMs)}`} />
            <Tile label="Latency, p95" value={formatMs(result.flow.latency.p95Ms)} detail={`p99 ${formatMs(result.flow.latency.p99Ms)}`} />
            <Tile
              label="Flows"
              value={`${formatNumber(result.flow.counts.completed)} completed`}
              detail={`${formatNumber(result.flow.counts.failed)} failed · ${result.drain.drained ? `drained in ${formatMs(result.drain.timeToDrainMs)}` : `${formatNumber(result.drain.leftInSystem)} left at the drain limit`}`}
            />
          </div>

          {olderEngine && (
            <p role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
              This run is from engine version {result.engineVersion}. In that version the completed work and the throughput of an element include calls that failed and calls that nobody waited for, and the bottleneck rule is another one. The numbers below are the numbers that it stored.
            </p>
          )}

          <Section id="stress-bottleneck" title="Bottleneck" rule={[bottleneck.rule, bottleneck.growthRule].filter(Boolean).join(' ')}>
            <BottleneckStatement result={result} resources={resources} />
          </Section>

          {result.capacity && (
            <Section id="stress-limit" title="Estimated limit" rule={result.capacity.rule}>
              <p className="rounded-md border border-border px-3 py-2 text-sm text-foreground">
                {result.capacity.flowsPerSecond === null
                  ? <>No resource served work in the measured window, so the run gives no limit.</>
                  : <>At about <span className="font-mono">{formatNumber(result.capacity.flowsPerSecond, 1)}</span> flows a second, <span className="font-medium">{result.capacity.name}</span> ({result.capacity.kind}) has every worker busy all the time. It runs out first.</>}
              </p>
            </Section>
          )}

          <Section id="stress-latency" title="Latency" note={`${result.methods.percentiles} ${result.flow.latency.reservoir || resources.some((r) => r.latency.reservoir) ? `* ${result.methods.sampling}` : ''}`}>
            <div className="overflow-x-auto rounded-md border border-border">
              <table className="w-full border-collapse text-xs text-muted-foreground">
                <thead>
                  <tr>{['', 'Samples', 'Min', 'Mean', 'p50', 'p90', 'p95', 'p99', 'Max'].map((h, i) => <th key={i} scope="col" className={th}>{h}</th>)}</tr>
                </thead>
                <tbody>
                  <LatencyRow name="Whole flow" kind="start to last synchronous step" latency={result.flow.latency} />
                  {result.operations.map((o) => <LatencyRow key={o.id} name={o.name} kind="call" latency={o.latency} />)}
                  {result.subscriptions.map((s) => <LatencyRow key={s.id} name={s.name} kind="publish to handled" latency={s.latency} />)}
                </tbody>
              </table>
            </div>
          </Section>

          <Section id="stress-resources" title="Utilisation and queues" note="Utilisation and queue depth cover the time from the end of the warm-up to the end of the arrivals. Completed is work that ended with success. Wasted is calls that an element finished after their caller had given up. Failed inside is calls that held a worker and failed because a step inside them failed.">
            <div className="overflow-x-auto rounded-md border border-border">
              <table className="w-full border-collapse text-xs text-muted-foreground">
                <thead>
                  <tr>{['', 'Workers', 'Utilisation', 'Mean wait', 'Mean queue', 'Max queue', 'Noise bound', 'Completed', 'Rejected', 'Dropped', 'Dead-lettered', 'Wasted', 'Failed inside'].map((h, i) => <th key={i} scope="col" className={th}>{h}</th>)}</tr>
                </thead>
                <tbody>
                  {resources.map((r) => (
                    <tr key={r.id} className="border-t border-border">
                      <th scope="row" className={`${td} font-normal text-foreground`}>
                        {r.name}
                        <span className="ml-1.5 text-[10px] text-muted-foreground">{r.kind === 'subscription' ? 'consumers' : 'element'}</span>
                        {bottleneck.id === r.id
                          ? <span className="ml-1.5 rounded-full border border-[var(--trace-retry)] px-1.5 text-[10px] text-[var(--trace-retry)]">bottleneck</span>
                          : r.saturation?.saturated && <span className="ml-1.5 rounded-full border border-border px-1.5 text-[10px] text-muted-foreground">saturated</span>}
                      </th>
                      <td className={td}>{r.workers}</td>
                      <td className={`${td} text-foreground`}>
                        <span className="inline-flex items-center gap-2">
                          <span aria-hidden className="inline-block h-1.5 w-14 overflow-hidden rounded-full bg-muted"><span className="block h-full rounded-full bg-[var(--chart-1)]" style={{ width: `${Math.min(100, r.utilisation * 100)}%` }} /></span>
                          {formatPercent(r.utilisation)}
                        </span>
                      </td>
                      <td className={td}>{formatMs(r.wait.meanMs)}</td>
                      <td className={td}>{formatNumber(r.queue.meanDepth, 1)}</td>
                      <td className={td}>{formatNumber(r.queue.maxDepth)}{r.queueCapacity !== null ? ` of ${r.queueCapacity}` : ''}</td>
                      <td className={td}>{formatNumber(r.saturation?.noiseBound)}</td>
                      <td className={td}>{formatNumber(r.counts.completed)}</td>
                      <td className={td}>{r.kind === 'element' ? formatNumber(r.counts.rejected) : '–'}</td>
                      <td className={td}>{r.kind === 'subscription' ? formatNumber(r.counts.dropped) : '–'}</td>
                      <td className={td}>{r.kind === 'subscription' ? formatNumber(r.counts.deadLettered) : '–'}</td>
                      <td className={td}>{r.kind === 'element' ? formatNumber(r.wastedCount) : '–'}</td>
                      <td className={td}>{r.kind === 'element' ? formatNumber(r.failedInside) : '–'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {result.operations.length > 0 && (
              <div className="overflow-x-auto rounded-md border border-border">
                <table className="w-full border-collapse text-xs text-muted-foreground">
                  <thead>
                    <tr>{['Operation', 'Attempts', 'Completed', 'Failed', 'Rejected', 'Timed out', 'Retries', 'Gave up', 'Timeout', 'Attempts allowed'].map((h, i) => <th key={i} scope="col" className={th}>{h}</th>)}</tr>
                  </thead>
                  <tbody>
                    {result.operations.map((o) => (
                      <tr key={o.id} className="border-t border-border">
                        <th scope="row" className={`${td} font-normal text-foreground`}>{o.name}</th>
                        {[o.attempts, o.counts.completed, o.counts.failed, o.counts.rejected, o.counts.timedOut, o.retries, o.exhausted].map((v, i) => <td key={i} className={td}>{formatNumber(v)}</td>)}
                        <td className={td}>{o.timeoutMs ? `${o.timeoutMs} ms` : 'none'}</td>
                        <td className={td}>{o.maxAttempts}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Section>

          <Section id="stress-charts" title="Over time" note={series.truncated ? 'The run went on after the last point that the series holds.' : undefined}>
            <div className="grid gap-3 lg:grid-cols-2">
              <LineChart
                title="Arrivals and throughput" unit="flows per second" intervalMs={series.intervalMs} series={rates} marks={marks}
                summary={`Flows that started, completed and failed in each interval of ${formatMs(series.intervalMs)}, as a rate per second.`}
              />
              {depths.length > 0 && (
                <LineChart
                  title="Queue depth and backlog" unit="waiting" intervalMs={series.intervalMs} series={depths} marks={marks}
                  summary={`Calls and messages that wait for a worker or a consumer at each instant.${hidden > 0 ? ` The ${depths.length} deepest queues of ${series.resources.length} are drawn.` : ''}`}
                />
              )}
              <LineChart
                title="Flows in flight" unit="flows" intervalMs={series.intervalMs} marks={marks}
                series={[{ name: 'In flight', color: SERIES[0], values: series.inFlight }]}
                summary="Flows that have started and whose last synchronous step has not ended."
              />
            </div>
          </Section>

          <Section id="stress-unmodelled" title="Not modelled" estimated={false} note="The estimate does not cover these effects. A running system has them.">
            <ul className="grid gap-x-6 gap-y-1 text-xs text-foreground md:grid-cols-2">
              {result.unmodelled.map((effect) => (
                <li key={effect.code} className="flex gap-2">
                  <span aria-hidden className="mt-1.5 h-1 w-1 shrink-0 rounded-full bg-muted-foreground" />
                  <span>{effect.text}{effect.kind === 'run' && <span className="ml-1 text-[10px] text-[var(--trace-retry)]">this run</span>}</span>
                </li>
              ))}
            </ul>
          </Section>

          <Section id="stress-confidence" title={`Confidence: ${result.confidence.level}`} estimated={false} note={result.confidence.rule}>
            <ul className="list-disc space-y-0.5 pl-4 text-xs text-muted-foreground">{result.confidence.reasons.map((reason, i) => <li key={i}>{reason}</li>)}</ul>
          </Section>

          <Section id="stress-assumptions" title="Assumptions" estimated={false} note="Every input of this run. A timeout or a retry policy comes from the model unless an override is listed.">
            {result.overrides.length > 0 && (
              <ul className="space-y-1 rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
                {result.overrides.map((o, i) => (
                  <li key={i}>Override: {o.name}, <span className="font-mono">{o.field}</span> is {o.profileValue}. The model declares {o.modelValue}.</li>
                ))}
              </ul>
            )}
            <div className="max-h-72 overflow-auto rounded-md border border-border">
              <table className="w-full border-collapse text-xs text-muted-foreground">
                <thead className="sticky top-0 bg-popover">
                  <tr><th scope="col" className={th}>Input</th><th scope="col" className={th}>Value</th><th scope="col" className={th}>Label</th></tr>
                </thead>
                <tbody>
                  {result.assumptions.map((a, i) => (
                    <tr key={i} className="border-t border-border">
                      <th scope="row" className={`${td} font-normal text-foreground`}>{a.name} · <span className="font-mono text-[11px]">{a.field}</span>{a.group === 'model-policy' && <span className="ml-1.5 text-[10px] text-muted-foreground">from the model</span>}</th>
                      <td className={`${td} text-foreground`}>{a.value}{a.unit ? ` ${a.unit}` : ''}</td>
                      <td className={td}>
                        {a.label}{a.measurementRef ? ` · ${a.measurementRef}` : ''}
                        {a.measurementStale && <span className="ml-1.5 rounded-full border border-[var(--trace-retry)] px-1.5 text-[10px] text-[var(--trace-retry)]">stale measurement</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-[11px] leading-relaxed text-muted-foreground">{result.methods.counting}</p>
          </Section>

          <Section id="stress-compare" title="Compare with another run" estimated={false}>
            {others.length === 0 ? (
              <p className="text-xs text-muted-foreground">This profile has no other stored run. Change an input, run the profile again, and compare the two runs here.</p>
            ) : (
              <label className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                Left side
                <select className={selectClass} value={other} onChange={(e) => setOther(e.target.value)}>
                  <option value="">Choose a run</option>
                  {others.map((r) => (
                    <option key={r.id} value={r.id}>{new Date(r.at).toLocaleString()} · profile v{r.profileVersion} · seed {r.seed}{r.outdated ? ' · outdated' : ''}</option>
                  ))}
                </select>
              </label>
            )}
            {comparison.isError && <p role="alert" className="text-xs text-destructive">{comparison.error.message}</p>}
            {comparison.data && other !== '' && <Comparison comparison={comparison.data} />}
          </Section>
        </div>
      </DialogContent>
    </Dialog>
  )
}
