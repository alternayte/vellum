import { memo, useCallback, useContext, useRef, useState } from 'react'
import type { Node, NodeProps } from '@xyflow/react'
import { SideHandles } from './side-handles'
import { Plus } from 'lucide-react'
import { elementColor, isExternal } from '@/lib/kind-colors'
import { KIND_WIDTHS } from '@/lib/card-size'
import { CONTAINMENT_RULES } from '@/lib/containment-rules'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'
import { useCanvasStore } from '@/stores/canvas-store'
import { cn } from '@/lib/utils'
import { factStatusColor, factStatusLabel, provenanceSummary, staleSummary, type Provenance, type StaleMark } from '@/lib/fact-status'
import type { DiffState } from '@/stores/draft-store'
import { StressCardRow } from '../stress-overlay'
import { StressOverlayContext } from '../stress-overlay-context'

export interface C4ElementData extends Record<string, unknown> {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  tags: string[]
  ownerId: string | null
  icon?: string | null
  factStatus?: string | null
  stale?: StaleMark | null
  /** A change of a Source to this fact, or a fact of it, waits for a person ("Needs you"). */
  needsYou?: boolean
  provenance?: Provenance | null
  lod?: 'full' | 'compact' | 'chip'
  diffState?: DiffState
  onRename?: (newName: string) => void
  /** How many elements this one holds. */
  childCount?: number
  /** Asks for a new element inside this one. Set for a system and an app. */
  onAddChild?: () => void
}

export type C4ElementNodeType = Node<C4ElementData, 'c4-element'>

const diffBorderColor: Record<string, string> = {
  added: '#2F855A',
  modified: '#B7791F',
  removed: '#B5483D',
  conflict: '#B7791F',
}

const KIND_LABELS: Record<string, string> = {
  actor: 'Actor',
  system: 'System',
  app: 'App',
  store: 'Store',
  component: 'Component',
}

export const C4ElementNode = memo(function C4ElementNode({ data }: NodeProps<C4ElementNodeType>) {
  const d = data
  const activeLens = useCanvasStore((s) => s.activeLens)
  const [isEditing, setIsEditing] = useState(false)
  const [editValue, setEditValue] = useState(d.name)
  const inputRef = useRef<HTMLInputElement>(null)
  const committedRef = useRef(false)

  const commitEdit = useCallback(() => {
    if (committedRef.current) return
    committedRef.current = true
    const trimmed = editValue.trim()
    if (trimmed && trimmed !== d.name) {
      d.onRename?.(trimmed)
    }
    setIsEditing(false)
    setEditValue(d.name)
  }, [editValue, d])

  const cancelEdit = useCallback(() => {
    committedRef.current = false
    setIsEditing(false)
    setEditValue(d.name)
  }, [d.name])

  const handleNameDoubleClick = useCallback((e: React.MouseEvent) => {
    e.stopPropagation()
    if (!d.onRename) return
    committedRef.current = false
    setEditValue(d.name)
    setIsEditing(true)
    setTimeout(() => inputRef.current?.select(), 0)
  }, [d.name, d.onRename])

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault()
      commitEdit()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      cancelEdit()
    }
  }, [commitEdit, cancelEdit])

  const iconName = d.icon || defaultIconForKind(d.kind)
  const IconComponent = getIconComponent(iconName)

  const showMetadata = activeLens === 'metadata'
  const showStatus = activeLens === 'status'
  const compact = d.lod === 'compact'
  const color = elementColor(d.kind, d.tags)
  const external = isExternal(d.tags)

  const borderStyle = showStatus && d.status === 'planned' ? 'dashed' : 'solid'
  const bgTint = showStatus && d.status === 'deprecated' ? 'color-mix(in srgb, var(--status-deprecated) 10%, transparent)' : undefined

  const diff = d.diffState
  const diffOverrideBorder = diff && diff !== 'unchanged' ? diffBorderColor[diff] : undefined
  const diffBg = diff === 'added' ? 'rgba(47, 133, 90, 0.05)' : bgTint
  // The technology chips of the metadata lens replace the technology in the kind line.
  const showChips = showMetadata && !!d.technology && !compact
  // Every card has the same height, so cards in a row line up and their edges run straight.
  // The add-inside row takes the second line of the description.
  // An external system is a service the team does not own: the card offers no inside to model.
  const addInside = !!d.onAddChild && d.childCount === 0 && !external
  // While a Stress run is open, a card with load in the run shows it in its last row.
  const stressLoad = useContext(StressOverlayContext)?.overlay.cards.has(d.id) ?? false

  return (
    <div
      className={cn(
        'group/node relative flex min-h-[128px] flex-col rounded-xl border border-border bg-card shadow-sm transition-shadow hover:shadow-md',
        diff === 'removed' && 'opacity-50',
        diff === 'unchanged' && 'opacity-60',
        d.kind === 'actor' && 'rounded-3xl',
      )}
      style={{
        width: `${KIND_WIDTHS[d.kind] ?? 264}px`,
        borderStyle,
        borderColor: diffOverrideBorder,
        borderWidth: diffOverrideBorder ? 2 : undefined,
        backgroundColor: diffBg,
      }}
    >
      <SideHandles className="h-2! w-2!" hoverClassName="group-hover/node:opacity-100" />

      <div className="flex flex-1 flex-col px-3.5 pb-3 pt-3">
        <div className="flex items-start gap-2.5">
          <span
            aria-hidden="true"
            className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border"
            style={{ color, borderColor: `color-mix(in oklab, ${color} 40%, transparent)`, backgroundColor: `color-mix(in oklab, ${color} 14%, transparent)` }}
          >
            {IconComponent && <IconComponent className="h-4 w-4" />}
          </span>
          <div className="min-w-0 flex-1">
            {isEditing ? (
              <input
                ref={inputRef}
                className="font-display w-full min-w-0 rounded bg-transparent px-1 -mx-1 text-[15px] font-semibold leading-5 text-foreground outline-none ring-1 ring-primary"
                value={editValue}
                onChange={(e) => setEditValue(e.target.value)}
                onKeyDown={handleKeyDown}
                onBlur={commitEdit}
                autoFocus
              />
            ) : (
              <span
                className={cn('font-display block truncate text-[15px] font-semibold leading-5 text-foreground', d.onRename && 'cursor-text')}
                title={d.name}
                onDoubleClick={handleNameDoubleClick}
              >
                {d.name}
              </span>
            )}
            <span className="mt-0.5 flex min-w-0 items-center gap-1.5 text-[11px] leading-4 text-muted-foreground">
              <span className="shrink-0 font-mono uppercase tracking-wider">{KIND_LABELS[d.kind] ?? d.kind}</span>
              {d.technology && !showChips && (
                <>
                  <span aria-hidden="true" className="shrink-0 opacity-60">·</span>
                  <span className="truncate" title={d.technology}>{d.technology}</span>
                </>
              )}
            </span>
          </div>
          <div className="flex shrink-0 items-center gap-1 pt-0.5">
            {d.onAddChild && (d.childCount ?? 0) > 0 && (
              <button
                type="button"
                aria-label={`Add an element inside ${d.name}`}
                title={`Add an element inside ${d.name}`}
                className="nodrag nopan flex h-5 w-5 items-center justify-center rounded text-muted-foreground opacity-0 hover:bg-muted hover:text-foreground focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-primary group-hover/node:opacity-100 pointer-coarse:opacity-100"
                onClick={(e) => { e.stopPropagation(); d.onAddChild?.() }}
                onDoubleClick={(e) => e.stopPropagation()}
              >
                <Plus className="h-3.5 w-3.5" aria-hidden="true" />
              </button>
            )}
            {diff === 'conflict' && (
              <span className="text-[12px]" title="Conflict">⚠</span>
            )}
            {d.needsYou && (
              <span
                className="rounded bg-[var(--trace-event)]/15 px-1 font-mono text-[9px] uppercase tracking-wider text-[var(--trace-event)]"
                title="Needs you: a Source changed this fact after a person edited it, or asks a question about it. Decide it in the Sources panel or the detail panel."
              >
                needs you
              </span>
            )}
            {external && (
              <span
                className="rounded border border-border bg-muted px-1 font-mono text-[9px] uppercase tracking-wider text-muted-foreground"
                title="External: a system outside this architecture's own code"
              >
                external
              </span>
            )}
            {d.stale && (
              <span
                className="whitespace-nowrap rounded border border-dashed border-[var(--trace-retry)] px-1 font-mono text-[9px] uppercase tracking-wider text-[var(--trace-retry)]"
                title={staleSummary(d.stale)}
              >
                not seen
              </span>
            )}
            {d.factStatus && d.factStatus !== 'declared' && (
              <span
                role="img"
                aria-label={factStatusLabel(d.factStatus)}
                className="inline-block h-2 w-2 shrink-0 rounded-full"
                style={{ backgroundColor: factStatusColor(d.factStatus) }}
                title={[factStatusLabel(d.factStatus), provenanceSummary(d.provenance)]
                  .filter(Boolean)
                  .join(' · ')}
              />
            )}
            {(d.childCount ?? 0) > 0 && (
              <span
                className="rounded-md bg-muted px-1.5 font-mono text-[10px] leading-4 tabular-nums text-muted-foreground"
                title={`${d.childCount} ${d.childCount === 1 ? 'element' : 'elements'} inside. Double-click to open.`}
              >
                {d.childCount}
              </span>
            )}
          </div>
        </div>

        {d.description && (
          <p className={cn('mt-2 text-[12.5px] leading-[1.4] text-muted-foreground', addInside ? 'mb-2.5 line-clamp-1' : stressLoad ? 'mb-2 line-clamp-1' : 'line-clamp-2')} title={d.description}>
            {d.description}
          </p>
        )}

        {addInside && !stressLoad && (
          <button
            type="button"
            className="nodrag nopan mt-auto flex w-full items-center gap-1.5 rounded-md border border-dashed border-border px-2 py-1 text-left text-xs text-muted-foreground hover:border-primary hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            onClick={(e) => { e.stopPropagation(); d.onAddChild?.() }}
            onDoubleClick={(e) => e.stopPropagation()}
          >
            <Plus className="h-3 w-3 shrink-0" aria-hidden="true" />
            Add {(CONTAINMENT_RULES[d.kind] ?? []).map((kind) => (/^[aeiou]/.test(kind) ? `an ${kind}` : `a ${kind}`)).join(' or ')} inside
          </button>
        )}

        {showChips && (
          <div className="mt-2 flex flex-wrap gap-1">
            {d.technology!.split(',').map((tech) => (
              <span
                key={tech.trim()}
                className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground"
              >
                {tech.trim()}
              </span>
            ))}
          </div>
        )}

        <StressCardRow id={d.id} name={d.name} />
      </div>
    </div>
  )
})
