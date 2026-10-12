import { useEffect } from 'react'
import { ChevronLeft, ChevronRight, CircleCheck, CircleX, Pause, Play, RotateCcw, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { describeEvent, formatVirtualTime, traceColor, traceFamily } from '@/lib/trace'
import { TRACE_SPEEDS, currentTraceEvent, useTraceStore } from '@/stores/trace-store'

const STEP_INTERVAL_MS = 900

/**
 * Playback transport, timeline and payload inspector for one trace run. Every event has a
 * sentence and a marker shape as well as a colour, so the trace reads without motion.
 */
export function TraceDock() {
  const { run, cursor, playing, speed, play, pause, seek, step, setSpeed, close } = useTraceStore()
  const event = useTraceStore(currentTraceEvent)

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
          <Button variant="ghost" size="icon" className="h-7 w-7 shrink-0" onClick={close} aria-label="Close trace">
            <X className="h-4 w-4" />
          </Button>
        </div>
      </section>
    )
  }

  return (
    <section aria-label="Trace playback" className="border-t border-border bg-card">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2 px-4 pt-2.5">
        <div className="flex items-center gap-1">
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => seek(-1)} aria-label="Restart">
            <RotateCcw className="h-3.5 w-3.5" />
          </Button>
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => { pause(); step(-1) }} disabled={cursor < 0} aria-label="Previous event">
            <ChevronLeft className="h-4 w-4" />
          </Button>
          <Button
            size="icon"
            className="h-8 w-8 rounded-full"
            onClick={playing ? pause : play}
            disabled={total === 0}
            aria-label={playing ? 'Pause' : 'Play'}
          >
            {playing ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4 translate-x-px" />}
          </Button>
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => { pause(); step(1) }} disabled={cursor >= total - 1} aria-label="Next event">
            <ChevronRight className="h-4 w-4" />
          </Button>
        </div>

        <div role="group" aria-label="Playback speed" className="flex items-center rounded-md border border-border p-0.5">
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

        <p className="font-mono text-[11px] tabular-nums text-muted-foreground">
          {Math.max(cursor + 1, 0)}/{total}
          <span className="mx-1.5 text-border">|</span>
          t = {formatVirtualTime(event?.atMs ?? 0)}
        </p>

        <div className="flex-1" />

        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 max-sm:order-last max-sm:w-full">
          {run.assertions.length > 0 && (
            <p
              className={cn(
                'flex items-center gap-1.5 text-xs',
                failedAssertions > 0 ? 'text-[var(--trace-failure)]' : 'text-[var(--trace-request)]',
              )}
            >
              {failedAssertions > 0 ? <CircleX className="h-3.5 w-3.5" /> : <CircleCheck className="h-3.5 w-3.5" />}
              {failedAssertions > 0
                ? `${failedAssertions} of ${run.assertions.length} expectations fail`
                : `${run.assertions.length} expectations hold`}
            </p>
          )}
          <p className="text-[11px] text-muted-foreground" title="A trace replays the declared contracts. It does not run or observe your system.">
            Declared behaviour, not a measurement
          </p>
        </div>
        <Button variant="ghost" size="icon" className="h-7 w-7" onClick={close} aria-label="Close trace">
          <X className="h-4 w-4" />
        </Button>
      </div>

      <div className="px-4 pb-1 pt-2">
        <ol aria-label="Trace events" className="flex h-7 items-center gap-px overflow-x-auto">
          {run.events.map((e, i) => {
            const family = traceFamily(e.kind)
            return (
              <li key={e.sequence} className="flex h-full min-w-2.5 flex-1">
                <button
                  type="button"
                  onClick={() => seek(i)}
                  aria-current={i === cursor ? 'step' : undefined}
                  aria-label={`${e.sequence}. ${describeEvent(e, run.labels)}`}
                  title={describeEvent(e, run.labels)}
                  className="group flex h-full w-full items-center focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-primary"
                >
                  <span
                    className={cn(
                      'block w-full transition-all',
                      // Failures are tall and square, retries are notched, the rest are bars:
                      // shape repeats what colour says.
                      family === 'failure' ? 'h-4 rounded-none' : family === 'retry' ? 'h-2 rounded-full' : 'h-3 rounded-sm',
                      i === cursor ? 'opacity-100 ring-2 ring-foreground ring-offset-1 ring-offset-card' : i < cursor ? 'opacity-90' : 'opacity-35 group-hover:opacity-70',
                    )}
                    style={{ backgroundColor: traceColor(e.kind) }}
                  />
                </button>
              </li>
            )
          })}
        </ol>
      </div>

      <div className="grid gap-x-6 gap-y-2 border-t border-border px-4 py-2.5 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="min-w-0" aria-live="polite">
          {event ? (
            <>
              <p className="flex items-center gap-2 text-sm text-foreground">
                <span className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: traceColor(event.kind) }} />
                <span className="truncate">{describeEvent(event, run.labels)}</span>
              </p>
              <p className="mt-1 truncate pl-4 font-mono text-[11px] text-muted-foreground">
                step {event.stepId}
                {event.detail && event.kind !== 'DataWritten' && event.kind !== 'BranchTaken' ? ` · ${event.detail}` : ''}
              </p>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">
              {total === 0 ? 'This scenario has no steps.' : 'Press play, or step through one event at a time.'}
            </p>
          )}
        </div>
        <div className="min-w-0">
          {event?.payload != null ? (
            <pre
              tabIndex={0}
              aria-label="Payload"
              className="max-h-28 overflow-auto rounded-md border border-border bg-background px-2.5 py-1.5 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            >
              {JSON.stringify(event.payload, null, 2)}
            </pre>
          ) : (
            event && <p className="text-xs text-muted-foreground">This event carries no payload.</p>
          )}
        </div>
      </div>
    </section>
  )
}
