import { useContext } from 'react'
import { Loader2, Pause } from 'lucide-react'
import { cn } from '@/lib/utils'
import { formatRate, type CardLoad } from '@/lib/stress-overlay'
import { useStressOverlayStore } from '@/stores/stress-overlay-store'
import { LOAD_COLOR, LOAD_LABEL, StressOverlayContext } from './stress-overlay-context'

const percent = (value: number) => `${Math.round(value * 100)}%`

function cardTitle(load: CardLoad): string {
  const lines = load.pools.map((p) =>
    `${p.name}${p.kind === 'subscription' ? ' (consumer)' : ''}: ${percent(p.utilisation)} of worker time, ${LOAD_LABEL[p.status]}`)
  return [`Estimated by the Stress run.${load.inside ? ' The highest load of the parts inside.' : ''}`, ...lines].join('\n')
}

/** The dot of a card at the zoom where it is a chip. */
export function StressChipDot({ id }: { id: string }) {
  const load = useContext(StressOverlayContext)?.overlay.cards.get(id)
  if (!load) return null
  return (
    <span
      role="img"
      aria-label={`Estimated load ${percent(load.utilisation)}, ${LOAD_LABEL[load.status]}`}
      title={cardTitle(load)}
      className="h-2 w-2 shrink-0 rounded-full ring-2 ring-card"
      style={{ backgroundColor: LOAD_COLOR[load.status] }}
    />
  )
}

/** The load row of a card: status dot, utilisation bar, and the button that fails the card. */
export function StressCardRow({ id, name }: { id: string; name: string }) {
  const value = useContext(StressOverlayContext)
  const load = value?.overlay.cards.get(id)
  if (!value || !load) return null
  const color = LOAD_COLOR[load.status]
  const running = value.failing === id
  return (
    <div className="mt-auto flex items-center gap-2 border-t border-border/70 pt-2 font-mono text-[10.5px] leading-4" title={cardTitle(load)}>
      <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: color }} />
      <span className="shrink-0 uppercase tracking-wider" style={{ color }}>{LOAD_LABEL[load.status]}</span>
      <span
        role="meter" aria-label={`Estimated utilisation of ${name}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(load.utilisation * 100)}
        className="relative h-1.5 min-w-8 flex-1 overflow-hidden rounded-full bg-muted"
      >
        <span className="absolute inset-y-0 left-0 rounded-full" style={{ width: `${Math.min(100, load.utilisation * 100)}%`, backgroundColor: color }} />
      </span>
      <span className="shrink-0 tabular-nums text-foreground">{percent(load.utilisation)}</span>
      <span className="shrink-0 text-muted-foreground">est.</span>
      {value.canFail && load.failable && (
        <button
          type="button"
          aria-label={`Fail ${name} for the whole run`}
          title={`Run the profile again with ${name} down for the whole run`}
          disabled={value.failing !== null}
          className={cn(
            'nodrag nopan -my-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded border border-border text-muted-foreground hover:border-[var(--trace-failure)] hover:text-[var(--trace-failure)] focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-50',
            running && 'text-[var(--trace-failure)]',
          )}
          onClick={(e) => { e.stopPropagation(); void useStressOverlayStore.getState().fail(id) }}
          onDoubleClick={(e) => e.stopPropagation()}
        >
          {running ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : <Pause className="h-3 w-3" aria-hidden="true" />}
        </button>
      )}
    </div>
  )
}

/** The rate pill of a line, placed at a point of the canvas. */
export function StressRatePill({ rate, x, y, zIndex }: { rate: number; x: number; y: number; zIndex?: number }) {
  return (
    <div
      className="nodrag nopan pointer-events-none absolute whitespace-nowrap rounded-full border border-[var(--trace-request)]/50 bg-card/95 px-1.5 py-px font-mono text-[10px] tabular-nums text-foreground shadow-sm"
      style={{ transform: `translate(-50%, 0) translate(${x}px, ${y + 14}px)`, zIndex }}
      title="Estimated work per second along this line in the Stress run: calls, writes and message deliveries, retries included"
    >
      {formatRate(rate)}/s <span className="text-muted-foreground">est.</span>
    </div>
  )
}
