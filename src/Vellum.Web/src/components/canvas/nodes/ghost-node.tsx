import { createElement, memo } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { Check, X, type LucideProps } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'
import { KIND_WIDTHS } from '@/lib/card-size'
import { CARD_SIDES, type CardSide } from '@/lib/edge-utils'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'
import { cn } from '@/lib/utils'
import { useGhostDecisions } from '@/hooks/use-ghost-layer'
import type { GhostNodeData } from '@/lib/ghost-layer'
import { useGhostStore } from '@/stores/ghost-store'
import { useShellStore } from '@/stores/shell-store'

export type GhostElementNodeType = Node<GhostNodeData, 'ghost-element'>
export type GhostContainerNodeType = Node<GhostNodeData, 'ghost-container'>

const POSITIONS: Record<CardSide, Position> = { top: Position.Top, right: Position.Right, bottom: Position.Bottom, left: Position.Left }

/** The ends of the ghost lines. A ghost starts no relationship, so the handles take no drag. */
function GhostHandles() {
  return (
    <>
      {CARD_SIDES.map((side) => (
        <Handle key={side} id={side} type="source" position={POSITIONS[side]} isConnectable={false} className="pointer-events-none! opacity-0!" />
      ))}
    </>
  )
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/**
 * Accept and reject of one ghost. They show on hover, on keyboard focus of the ghost or of a
 * button, on a touch screen, and on the selected ghost.
 */
export function GhostActions({ data, visible, className }: { data: GhostNodeData; visible?: boolean; className?: string }) {
  const { accept, reject } = useGhostDecisions()
  const busy = useGhostStore((s) => s.busy)
  if (!data.canDecide) return null
  const acceptTitle = data.needs > 0 ? `Accept ${data.name}, and ${plural(data.needs, 'item', 'items')} it needs` : `Accept ${data.name}`
  const rejectTitle = data.neededBy > 0 ? `Reject ${data.name}, and ${plural(data.neededBy, 'item that needs', 'items that need')} it` : `Reject ${data.name}`
  const button = 'nodrag nopan flex h-6 w-6 items-center justify-center rounded-md border bg-card text-muted-foreground focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-50'
  return (
    <div
      className={cn(
        // The class lets the focus of the canvas node show them too (tailwind.css).
        'vellum-ghost-actions flex shrink-0 items-center gap-1 transition-opacity',
        visible ? 'opacity-100' : 'opacity-0 group-hover/ghost:opacity-100 group-focus-within/ghost:opacity-100 pointer-coarse:opacity-100',
        className,
      )}
    >
      <button
        type="button" aria-label={acceptTitle} title={acceptTitle} disabled={busy}
        className={cn(button, 'border-[var(--trace-request)]/50 hover:bg-[var(--trace-request)]/15 hover:text-[var(--trace-request)]')}
        onClick={(e) => { e.stopPropagation(); void accept(data.itemId) }}
        onDoubleClick={(e) => e.stopPropagation()}
      >
        <Check className="h-3.5 w-3.5" aria-hidden="true" />
      </button>
      <button
        type="button" aria-label={rejectTitle} title={rejectTitle} disabled={busy}
        className={cn(button, 'border-border hover:bg-destructive/15 hover:text-destructive')}
        onClick={(e) => { e.stopPropagation(); void reject(data.itemId) }}
        onDoubleClick={(e) => e.stopPropagation()}
      >
        <X className="h-3.5 w-3.5" aria-hidden="true" />
      </button>
    </div>
  )
}

/** The reasons an item cannot be accepted: a refusal of the server, or a check against the model as it is now. */
export function GhostProblems({ data }: { data: GhostNodeData }) {
  if (data.refused.length === 0 && data.issues.length === 0) return null
  return (
    <ul className="mt-1.5 space-y-0.5 text-[11px] leading-snug">
      {data.refused.map((reason, i) => <li key={`r${i}`} className="text-destructive">Refused: {reason}</li>)}
      {data.issues.map((reason, i) => <li key={`i${i}`} className="text-[var(--trace-retry)]">Cannot be added now: {reason}</li>)}
    </ul>
  )
}

const KIND_ICONS = new Map(['actor', 'system', 'app', 'store', 'component'].map((kind) => [kind, getIconComponent(defaultIconForKind(kind))]))

function KindIcon({ kind, ...props }: { kind: string } & LucideProps) {
  const icon = KIND_ICONS.get(kind)
  return icon ? createElement(icon, props) : null
}

const KIND_LABELS: Record<string, string> = { actor: 'Actor', system: 'System', app: 'App', store: 'Store', component: 'Component' }

function frameStyle(kind: string, refused: boolean, tint: number) {
  const color = kindColor(kind)
  return {
    borderWidth: '1.5px',
    borderStyle: 'dashed' as const,
    borderColor: refused ? 'var(--destructive)' : `color-mix(in oklab, ${color} 70%, transparent)`,
    backgroundColor: `color-mix(in oklab, ${color} ${tint}%, color-mix(in oklab, var(--card) 70%, transparent))`,
  }
}

function Proposed({ data }: { data: GhostNodeData }) {
  return (
    <span className="font-mono text-[10px] text-[var(--trace-retry)]" title="Proposed, not in the model. The confidence is what the author of the proposal claims. It is not a measurement.">
      proposed · {data.alias} · {Math.round(data.confidence * 100)}%
    </span>
  )
}

/** A proposed element that is not in the model: a dashed card. */
export const GhostElementNode = memo(function GhostElementNode({ data }: NodeProps<GhostElementNodeType>) {
  const selected = useShellStore((s) => s.selectedGhostItemId === data.itemId)
  const color = kindColor(data.kind)
  const refused = data.refused.length > 0
  return (
    <div
      className={cn('group/ghost relative flex min-h-[128px] flex-col rounded-xl px-3.5 pb-3 pt-3', data.kind === 'actor' && 'rounded-3xl', selected && 'ring-2 ring-primary')}
      style={{ width: `${KIND_WIDTHS[data.kind] ?? 264}px`, ...frameStyle(data.kind, refused, 6) }}
    >
      <GhostHandles />
      <div className="flex items-start gap-2.5">
        <span
          aria-hidden="true"
          className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-dashed"
          style={{ color, borderColor: `color-mix(in oklab, ${color} 50%, transparent)` }}
        >
          <KindIcon kind={data.kind} className="h-4 w-4" />
        </span>
        <div className="min-w-0 flex-1">
          <span className="font-display block truncate text-[15px] font-semibold leading-5 text-foreground/85" title={data.name}>{data.name}</span>
          <span className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[11px] leading-4 text-muted-foreground">
            <span className="shrink-0 font-mono uppercase tracking-wider">{KIND_LABELS[data.kind] ?? data.kind}</span>
            {data.technology && (
              <>
                <span aria-hidden="true" className="shrink-0 opacity-60">·</span>
                <span className="truncate" title={data.technology}>{data.technology}</span>
              </>
            )}
          </span>
        </div>
        <GhostActions data={data} visible={selected} />
      </div>
      <div className="mt-auto pt-2"><Proposed data={data} /></div>
      <GhostProblems data={data} />
    </div>
  )
})

/** A proposed element that holds other proposed elements: a dashed frame around them. */
export const GhostContainerNode = memo(function GhostContainerNode({ data }: NodeProps<GhostContainerNodeType>) {
  const selected = useShellStore((s) => s.selectedGhostItemId === data.itemId)
  const color = kindColor(data.kind)
  return (
    <div
      className={cn('group/ghost h-full w-full rounded-2xl px-4 pt-3', selected && 'ring-2 ring-primary')}
      style={frameStyle(data.kind, data.refused.length > 0, 3)}
    >
      <GhostHandles />
      <div className="flex items-center gap-2">
        <KindIcon kind={data.kind} className="h-4 w-4 shrink-0" style={{ color }} aria-hidden="true" />
        <span className="font-display truncate text-sm font-semibold text-foreground/85">{data.name}</span>
        <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{data.kind}</span>
        <Proposed data={data} />
        <span className="flex-1" />
        <GhostActions data={data} visible={selected} />
      </div>
      <GhostProblems data={data} />
    </div>
  )
})
