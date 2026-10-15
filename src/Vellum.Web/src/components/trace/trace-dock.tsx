import { useEffect, type ReactNode } from 'react'
import { ChevronLeft, ChevronRight, CircleCheck, CircleX, Pause, Play, RotateCcw, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { describeEvent, formatVirtualTime, notRoutedReason, traceColor, traceFamily, type TraceEventKind, type TraceFamily } from '@/lib/trace'
import { CERTAINTY, type StepImpact } from '@/lib/impact'
import { TRACE_SPEEDS, currentTraceEvent, useTraceStore } from '@/stores/trace-store'

const STEP_INTERVAL_MS = 900

/**
 * Playback transport, timeline and payload inspector for one trace run. Every event has a
 * sentence and a marker shape as well as a colour, so the trace reads without motion.
 */
export function TraceDock() {
  const { run, cursor, playing, speed, play, pause, seek, step, setSpeed, close } = useTraceStore()
  const event = useTraceStore(currentTraceEvent)
  const impact = useTraceStore((s) => s.impact)

  useEffect(() => {
    if (!playing) return
    const timer = window.setInterval(() => useTraceStore.getState().step(1), STEP_INTERVAL_MS / speed)
    return () => window.clearInterval(timer)
  }, [playing, speed])

  if (!run) return null

  const total = run.events.length
  const failedAssertions = run.assertions.filter((a) => !a.passed).length

  if (run.issues.length > 0) {
    return (
      <section aria-label="Trace playback" className="border-t border-border bg-card px-4 py-3">
        <div className="flex items-start gap-3">
          <div className="min-w-0 flex-1">
            <h2 className="font-heading text-sm font-medium text-foreground">This scenario cannot play</h2>
            <p className="mt-0.5 text-xs text-muted-foreground">
              Fix {run.issues.length === 1 ? 'this reference' : `these ${run.issues.length} references`} first. Nothing ran.
            </p>
            <ul className="mt-2 max-h-28 space-y-1 overflow-y-auto text-xs">
              {run.issues.map((issue, i) => (
                <li key={i} className="flex gap-2">
                  <span className="shrink-0 font-mono text-[var(--trace-failure)]">{issue.stepId || 'scenario'}</span>
                  <span className="text-foreground">{issue.message}</span>
                </li>
              ))}
            </ul>
          </div>
          <Button variant="ghost" size="icon" className="h-7 w-7 shrink-0" onClick={close} aria-label="Close trace" title="Close trace">
            <X className="h-4 w-4" />
          </Button>
        </div>
      </section>
    )
  }

  return (
    <TooltipProvider delayDuration={300}>
    <section aria-label="Trace playback" className="border-t border-border bg-card">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 pt-2.5">
        <div className="flex items-center gap-1">
          <DockControl label="Restart from the first event">
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => seek(-1)} aria-label="Restart">
              <RotateCcw className="h-3.5 w-3.5" />
            </Button>
          </DockControl>
          <DockControl label="Previous event">
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => { pause(); step(-1) }} disabled={cursor < 0} aria-label="Previous event">
              <ChevronLeft className="h-4 w-4" />
            </Button>
          </DockControl>
          <DockControl label={playing ? 'Pause' : 'Play'}>
            <Button
              size="icon"
              className="h-8 w-8 rounded-full"
              onClick={playing ? pause : play}
              disabled={total === 0}
              aria-label={playing ? 'Pause' : 'Play'}
            >
              {playing ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4 translate-x-px" />}
            </Button>
          </DockControl>
          <DockControl label="Next event">
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => { pause(); step(1) }} disabled={cursor >= total - 1} aria-label="Next event">
              <ChevronRight className="h-4 w-4" />
            </Button>
          </DockControl>
        </div>

        <div role="group" aria-label="Playback speed" className="flex items-center gap-1.5">
          <span aria-hidden="true" className="text-[11px] text-muted-foreground">Speed</span>
          <div className="flex items-center rounded-md border border-border p-0.5">
          {TRACE_SPEEDS.map((s) => (
            <button
              key={s}
              type="button"
              aria-pressed={speed === s}
              onClick={() => setSpeed(s)}
              className={cn(
                'rounded px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                speed === s && 'bg-muted text-foreground',
              )}
            >
              {s}×
            </button>
          ))}
          </div>
        </div>

        <p className="text-[11px] tabular-nums text-muted-foreground">
          Event <span className="font-mono text-foreground">{Math.max(cursor + 1, 0)}</span> of <span className="font-mono">{total}</span>
          <span className="mx-1.5 text-border">|</span>
          <span className="font-mono">t = {formatVirtualTime(event?.atMs ?? 0)}</span>
        </p>

        <div className="flex-1" />

        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 max-sm:order-last max-sm:w-full">
          {run.assertions.length > 0 && (
            <details className="relative">
              <summary
                className={cn(
                  'flex cursor-pointer list-none items-center gap-1.5 rounded text-xs underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary',
                  failedAssertions > 0 ? 'text-[var(--trace-failure)]' : 'text-[var(--trace-request)]',
                )}
              >
                {failedAssertions > 0 ? <CircleX className="h-3.5 w-3.5" /> : <CircleCheck className="h-3.5 w-3.5" />}
                {failedAssertions > 0
                  ? `${failedAssertions} of ${run.assertions.length} expectations fail`
                  : `${run.assertions.length} expectations hold`}
              </summary>
              <ul className="absolute bottom-full right-0 z-20 mb-2 w-80 max-w-[calc(100vw-2rem)] space-y-1.5 rounded-lg border border-border bg-popover p-3 text-xs shadow-xl max-sm:left-0 max-sm:right-auto">
                {run.assertions.map((assertion) => (
                  <li key={assertion.id} className="flex gap-2">
                    {assertion.passed
                      ? <CircleCheck className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--trace-request)]" aria-label="Holds" />
                      : <CircleX className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--trace-failure)]" aria-label="Fails" />}
                    <span className="min-w-0">
                      <span className="font-mono text-foreground">{assertion.id}</span>
                      <span className="block text-muted-foreground">{assertion.detail}</span>
                    </span>
                  </li>
                ))}
              </ul>
            </details>
          )}
          <p className="text-[11px] text-muted-foreground" title="A trace replays the declared contracts. It does not run or observe your system.">
            Declared behaviour, not a measurement
          </p>
        </div>
        <DockControl label="Close trace">
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={close} aria-label="Close trace">
            <X className="h-4 w-4" />
          </Button>
        </DockControl>
      </div>

      {impact && <AffectedSteps impact={impact.steps} change={impact.change} onSeek={(stepId) => {
        const index = run.events.findIndex((e) => e.stepId === stepId)
        if (index >= 0) seek(index)
      }} />}

      <div className="px-4 pb-1 pt-2">
        <ol aria-label="Trace events" className={cn('flex items-center gap-px overflow-x-auto', impact ? 'h-9' : 'h-7')}>
          {run.events.map((e, i) => {
            const family = traceFamily(e.kind)
            const affected = impact?.steps[e.stepId]
            return (
              <li key={e.sequence} className="flex h-full min-w-2.5 flex-1">
                <button
                  type="button"
                  onClick={() => seek(i)}
                  aria-current={i === cursor ? 'step' : undefined}
                  aria-label={`${e.sequence}. ${describeEvent(e, run.labels)}${affected ? `. Impact: ${CERTAINTY[affected.certainty].label}` : ''}`}
                  title={`${describeEvent(e, run.labels)}${affected ? ` · impact ${CERTAINTY[affected.certainty].label.toLowerCase()}` : ''}`}
                  className="group flex h-full w-full flex-col items-stretch justify-center gap-1 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-primary"
                >
                  <span
                    className={cn(
                      'block w-full transition-all',
                      // Failures are tall and square, retries are notched, the rest are bars:
                      // shape repeats what colour says.
                      family === 'failure' ? 'h-4 rounded-none' : family === 'retry' ? 'h-2 rounded-full' : 'h-3 rounded-sm',
                      i === cursor
                        ? 'opacity-100 ring-2 ring-foreground ring-offset-1 ring-offset-card'
                        : cn(markerOpacity(family, i < cursor), 'group-hover:opacity-80'),
                    )}
                    style={{ backgroundColor: markerColor(e.kind, i === cursor) }}
                  />
                  {impact && (
                    // The step of this event is affected by the change: the line has the certainty's colour and style.
                    <span
                      aria-hidden="true"
                      className={cn('block h-0 w-full border-t-2', !affected && 'border-transparent')}
                      style={affected ? { borderColor: CERTAINTY[affected.certainty].color, borderTopStyle: CERTAINTY[affected.certainty].line } : undefined}
                    />
                  )}
                </button>
              </li>
            )
          })}
        </ol>
      </div>

      <div className="grid items-start gap-x-6 gap-y-3 border-t border-border px-4 py-2.5 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="min-w-0" aria-live="polite">
          <h3 className={PANE_HEADING}>Event</h3>
          {event ? (
            <>
              <p className="flex items-center gap-2 text-sm text-foreground">
                <span className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: traceColor(event.kind) }} />
                <span className="truncate">{describeEvent(event, run.labels)}</span>
              </p>
              <p className="mt-1 truncate pl-4 font-mono text-[11px] text-muted-foreground">
                step {event.stepId}
                {event.kind === 'NotRouted'
                  ? ` · ${notRoutedReason(event.detail)}`
                  : event.detail && event.kind !== 'DataWritten' && event.kind !== 'BranchTaken' ? ` · ${event.detail}` : ''}
              </p>
              {impact?.steps[event.stepId] && <EventImpact step={impact.steps[event.stepId]} />}
              {event.headers && (
                <dl className="mt-1.5 flex flex-wrap gap-1.5 pl-4" aria-label="Headers">
                  {Object.entries(event.headers).map(([header, value]) => (
                    <div key={header} className="flex rounded border border-border font-mono text-[10px]">
                      <dt className="border-r border-border px-1.5 py-0.5 text-muted-foreground">{header}</dt>
                      <dd className="px-1.5 py-0.5 text-foreground">{value}</dd>
                    </div>
                  ))}
                </dl>
              )}
            </>
          ) : (
            <p className="text-sm text-muted-foreground">
              {total === 0 ? 'This scenario has no steps.' : 'Press play, or step through one event at a time.'}
            </p>
          )}
        </div>
        <div className="min-w-0">
          <h3 className={PANE_HEADING}>Payload</h3>
          {event?.payload != null ? (
            <pre
              tabIndex={0}
              aria-label="Payload"
              className="max-h-28 overflow-auto rounded-md border border-border bg-background px-2.5 py-1.5 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            >
              {JSON.stringify(event.payload, null, 2)}
            </pre>
          ) : (
            <p className="text-sm text-muted-foreground">{event ? 'None. This event carries no payload.' : 'Shown for the event in focus.'}</p>
          )}
        </div>
      </div>
    </section>
    </TooltipProvider>
  )
}

const PANE_HEADING = 'mb-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground'

/** Failures and retries keep their colour on the timeline: they are what a reader looks for. */
function meaningful(family: TraceFamily): boolean {
  return family === 'failure' || family === 'retry'
}

/**
 * One accent for the event in focus; the rest of the timeline is neutral, except failures and
 * retries, whose colour carries meaning. Shape and the event sentence repeat what colour says.
 */
function markerColor(kind: TraceEventKind, current: boolean): string {
  return current || meaningful(traceFamily(kind)) ? traceColor(kind) : 'var(--muted-foreground)'
}

/** Played events are stronger than the ones still to come. */
function markerOpacity(family: TraceFamily, played: boolean): string {
  if (meaningful(family)) return played ? 'opacity-90' : 'opacity-45'
  return played ? 'opacity-50' : 'opacity-20'
}

/** An icon control with a text tooltip that names what it does. */
function DockControl({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>{children}</TooltipTrigger>
      <TooltipContent side="top" sideOffset={6}>{label}</TooltipContent>
    </Tooltip>
  )
}

/**
 * The steps of the flow that the change reaches, in the order the scenario writes them.
 * Each says how sure the impact is; a click moves the playback to the step's first event.
 */
function AffectedSteps({ impact, change, onSeek }: { impact: Record<string, StepImpact>; change: string; onSeek: (stepId: string) => void }) {
  const steps = Object.values(impact)
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 px-4 pt-2 text-xs">
      <span className="text-muted-foreground">Impact of {change}:</span>
      {steps.length === 0 && <span className="text-muted-foreground">no step of this flow is affected.</span>}
      <ol aria-label="Affected steps" className="contents">
        {steps.map((step) => {
          const look = CERTAINTY[step.certainty]
          return (
            <li key={step.stepId}>
              <button
                type="button"
                onClick={() => onSeek(step.stepId)}
                title={look.meaning}
                className="rounded-full border px-2 py-0.5 text-[11px] hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                style={{ borderColor: look.color, borderStyle: look.line }}
              >
                <span className="font-mono text-foreground">{step.stepId}</span>
                {step.label && <span className="text-muted-foreground"> · {step.label}</span>}
                <span style={{ color: look.color }}> · {look.label}</span>
              </button>
            </li>
          )
        })}
      </ol>
    </div>
  )
}

/** What the change does to the step of the current event, with a link to the source lines of each change it stands on. */
function EventImpact({ step }: { step: StepImpact }) {
  const look = CERTAINTY[step.certainty]
  return (
    <div className="mt-1.5 pl-4 text-xs">
      <p>
        <span className="rounded-full border px-1.5 text-[11px]" style={{ borderColor: look.color, borderStyle: look.line, color: look.color }}>
          Impact: {look.label}
        </span>
      </p>
      <ul className="mt-1 space-y-0.5 text-muted-foreground">
        {step.reasons.map((reason, i) => {
          const evidence = reason.change?.evidence[0]
          return (
            <li key={i} className="truncate" title={reason.text}>
              <span style={{ color: CERTAINTY[reason.certainty].color }}>{CERTAINTY[reason.certainty].label}</span>
              {' through '}<span className="text-foreground">{reason.change?.name ?? 'a change'}</span>
              {evidence && (
                <>
                  {' · '}
                  <a href={evidence.url ?? undefined} target="_blank" rel="noreferrer noopener" title={evidence.pointer} className="font-mono underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary">
                    {evidence.path}:{evidence.startLine}
                  </a>
                </>
              )}
            </li>
          )
        })}
      </ul>
    </div>
  )
}
