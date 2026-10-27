import { useEffect, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Loader2, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { formatMs, formatPercent } from '@/lib/stress-api'
import { BUSY_FROM, formatRate, stressStrip } from '@/lib/stress-overlay'
import { LOAD_COLOR, LOAD_LABEL } from '@/components/canvas/stress-overlay-context'
import { useStressOverlayStore } from '@/stores/stress-overlay-store'
import { StressResultDialog } from './stress-result-dialog'

function Figure({ label, value, detail, title }: { label: string; value: string; detail: string; title?: string }) {
  return (
    <div className="min-w-0 rounded-md border border-border px-2 py-1 sm:px-2.5 sm:py-1.5" title={title ?? detail}>
      <p className="truncate font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{label}</p>
      <p className="truncate font-mono text-sm leading-5 tabular-nums text-foreground sm:text-base sm:leading-6">{value}</p>
      <p className="truncate text-[10px] text-muted-foreground sm:text-[11px]">{detail}</p>
    </div>
  )
}

/**
 * The strip under the canvas while a Stress run is drawn on it: the six headline numbers of the
 * run, what pins it, and the way back. Every number is the engine's estimate.
 */
export function StressDock({ projectId, hidden = false }: { projectId: string; hidden?: boolean }) {
  const open = useStressOverlayStore((s) => s.open)
  const failing = useStressOverlayStore((s) => s.failing)
  const error = useStressOverlayStore((s) => s.error)
  const { close, back } = useStressOverlayStore.getState()
  const [dialog, setDialog] = useState(false)
  const queryClient = useQueryClient()

  // The overlay belongs to this project's canvas; leaving it closes the overlay.
  useEffect(() => () => useStressOverlayStore.getState().close(), [projectId])
  // A failed card stores a run: the lists of runs show it.
  const runId = open?.run.run.id
  useEffect(() => {
    if (runId) queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'stress'] })
  }, [runId, projectId, queryClient])

  if (!open || open.projectId !== projectId || hidden) return null
  const { run, result } = open.run
  const strip = stressStrip(result)
  const failed = run.summary.failedElement
  const failingName = failing ? (result.elements.find((e) => e.id === failing)?.name ?? result.subscriptions.find((s) => s.consumerId === failing)?.name ?? 'the element') : null

  return (
    <section aria-label="Stress run on the canvas" className="border-t border-border bg-card px-3 py-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h2 className="flex min-w-0 items-center gap-2 text-xs">
          <span className="font-mono uppercase tracking-wider text-muted-foreground">Stress</span>
          <span className="truncate font-medium text-foreground">{open.profileName}</span>
          <span className="shrink-0 rounded-full border border-[var(--trace-retry)] px-1.5 py-px text-[10px] font-medium uppercase tracking-wider text-[var(--trace-retry)]">Estimated</span>
        </h2>
        {failed && (
          <span className="rounded-full border border-[var(--trace-failure)] px-1.5 text-[11px] text-[var(--trace-failure)]">{failed} down for the whole run</span>
        )}
        {run.outdated && (
          <span className="rounded-full border border-[var(--trace-retry)] px-1.5 text-[11px] text-[var(--trace-retry)]" title={`The ${run.outdatedBecause.join(' and the ')} changed after this run.`}>outdated</span>
        )}
        <ul aria-label="Card status" className="hidden items-center gap-2.5 text-[11px] text-muted-foreground lg:flex">
          {(['ok', 'busy', 'saturated', 'down'] as const).map((status) => (
            <li key={status} className="flex items-center gap-1" title={status === 'busy' ? `${formatPercent(BUSY_FROM)} of worker time or more` : status === 'saturated' ? 'Named by the saturation rule of the engine' : undefined}>
              <span aria-hidden="true" className="h-2 w-2 rounded-full" style={{ backgroundColor: LOAD_COLOR[status] }} />
              {LOAD_LABEL[status]}
            </li>
          ))}
        </ul>
        <div className="ml-auto flex items-center gap-1">
          {open.previous && (
            <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={back} title="Show the run before the failed card again">
              <ArrowLeft className="h-3 w-3" aria-hidden="true" /> Back
            </Button>
          )}
          <Button variant="ghost" size="sm" className="h-6 px-1.5 font-mono text-[11px] text-muted-foreground hover:text-foreground" onClick={() => setDialog(true)} title="Open the stored run">
            seed {run.seed} · v{run.profileVersion} · {run.hash.slice(0, 8)}
          </Button>
          <Button variant="ghost" size="icon" className="h-6 w-6" onClick={close} aria-label="Close the Stress overlay" title="Close the Stress overlay">
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>

      {(failing || error) && (
        <p role={error ? 'alert' : 'status'} className={`mt-1 flex items-center gap-1.5 text-xs ${error ? 'text-[var(--trace-failure)]' : 'text-muted-foreground'}`}>
          {failing && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
          {failing ? `Running the profile with ${failingName} down for the whole run…` : error}
        </p>
      )}

      <div className="mt-1.5 grid grid-cols-3 gap-1.5 xl:grid-cols-6">
        <Figure label="Offered" value={`${formatRate(strip.offeredPerSecond)}/s`} detail="flows started, estimated" />
        <Figure label="Goodput" value={`${formatRate(strip.goodputPerSecond)}/s`} detail="flows completed, estimated" />
        <Figure label="Success rate" value={strip.successRate === null ? '–' : formatPercent(strip.successRate)} detail="of finished flows, estimated" />
        <Figure label="p95 latency" value={formatMs(strip.p95Ms)} detail="whole flow, estimated" />
        <Figure
          label="Busiest"
          value={strip.busiest ? formatPercent(strip.busiest.utilisation) : '–'}
          detail={strip.busiest ? `${strip.busiest.name}, estimated` : 'no resource, estimated'}
        />
        <Figure
          label="Limit"
          value={strip.limit?.flowsPerSecond != null ? `${formatRate(strip.limit.flowsPerSecond)}/s` : '–'}
          detail={!strip.limit
            ? `engine ${result.engineVersion} has none`
            : strip.limit.name ? `${strip.limit.name} runs out first, estimated` : 'nothing served work, estimated'}
          title={strip.limit?.rule ?? 'Run the profile again: this run is from an engine version without an estimate of the limit.'}
        />
      </div>

      {dialog && (
        <StressResultDialog projectId={projectId} profileName={open.profileName} run={open.run} runs={[]} onClose={() => setDialog(false)} />
      )}
    </section>
  )
}
