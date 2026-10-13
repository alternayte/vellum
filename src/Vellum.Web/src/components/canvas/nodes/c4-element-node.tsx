import { memo, useCallback, useRef, useState } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { Plus } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'
import { CONTAINMENT_RULES } from '@/lib/containment-rules'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'
import { useCanvasStore } from '@/stores/canvas-store'
import { cn } from '@/lib/utils'
import { factStatusColor, factStatusLabel, provenanceSummary, staleSummary, type Provenance, type StaleMark } from '@/lib/fact-status'
import type { DiffState } from '@/stores/draft-store'

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

const KIND_WIDTHS: Record<string, number> = {
  actor: 240,
  system: 320,
  app: 280,
  store: 280,
  component: 240,
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

  const borderStyle = showStatus && d.status === 'planned' ? 'dashed' : 'solid'
  const bgTint = showStatus && d.status === 'deprecated' ? 'color-mix(in srgb, var(--status-deprecated) 10%, transparent)' : undefined

  const diff = d.diffState
  const diffOverrideBorder = diff && diff !== 'unchanged' ? diffBorderColor[diff] : undefined
  const diffBg = diff === 'added' ? 'rgba(47, 133, 90, 0.05)' : bgTint

  return (
    <div
      className={cn(
        'group/node relative rounded-lg border border-border bg-card shadow-sm transition-shadow hover:shadow-md',
        diff === 'removed' && 'opacity-50',
        diff === 'unchanged' && 'opacity-60',
        d.kind === 'actor' && 'rounded-2xl',
      )}
      style={{
        width: `${KIND_WIDTHS[d.kind] ?? 280}px`,
        borderLeftWidth: '4px',
        borderLeftColor: diffOverrideBorder ?? kindColor(d.kind),
        borderLeftStyle: 'solid',
        borderTopStyle: borderStyle,
        borderRightStyle: borderStyle,
        borderBottomStyle: borderStyle,
        borderTopColor: diffOverrideBorder,
        borderRightColor: diffOverrideBorder,
        borderBottomColor: diffOverrideBorder,
        backgroundColor: diffBg,
      }}
    >
      <Handle type="target" position={Position.Top} className="h-2! w-2! bg-primary! opacity-0 transition-opacity group-hover/node:opacity-100" />
      <Handle type="source" position={Position.Right} className="h-2! w-2! bg-primary! opacity-0 transition-opacity group-hover/node:opacity-100" />
      <Handle type="target" position={Position.Bottom} className="h-2! w-2! bg-primary! opacity-0 transition-opacity group-hover/node:opacity-100" />
      <Handle type="source" position={Position.Left} className="h-2! w-2! bg-primary! opacity-0 transition-opacity group-hover/node:opacity-100" />

      <div className="p-3">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-1.5">
            {IconComponent && <IconComponent className="h-4 w-4 text-muted-foreground shrink-0" />}
            {isEditing ? (
              <input
                ref={inputRef}
                className="font-display w-full min-w-0 bg-transparent text-sm font-medium text-foreground outline-none ring-1 ring-primary rounded px-1 -mx-1"
                value={editValue}
                onChange={(e) => setEditValue(e.target.value)}
                onKeyDown={handleKeyDown}
                onBlur={commitEdit}
                autoFocus
              />
            ) : (
              <span
                className={cn("font-display text-sm font-medium text-foreground", d.onRename && "cursor-text")}
                onDoubleClick={handleNameDoubleClick}
              >
                {d.name}
              </span>
            )}
          </div>
          <div className="flex items-center gap-1">
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
            {d.stale && (
              <span
                className="rounded border border-dashed border-[var(--trace-retry)] px-1 font-mono text-[9px] uppercase tracking-wider text-[var(--trace-retry)]"
                title={staleSummary(d.stale)}
              >
                stale
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
            <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
              {d.kind}
            </span>
          </div>
        </div>

        {d.description && !compact && (
          <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
            {d.description}
          </p>
        )}

        {d.onAddChild && d.childCount === 0 && (
          <button
            type="button"
            className="nodrag nopan mt-2 flex w-full items-center gap-1.5 rounded-md border border-dashed border-border px-2 py-1 text-left text-xs text-muted-foreground hover:border-primary hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            onClick={(e) => { e.stopPropagation(); d.onAddChild?.() }}
            onDoubleClick={(e) => e.stopPropagation()}
          >
            <Plus className="h-3 w-3 shrink-0" aria-hidden="true" />
            Add {(CONTAINMENT_RULES[d.kind] ?? []).map((kind) => (/^[aeiou]/.test(kind) ? `an ${kind}` : `a ${kind}`)).join(' or ')} inside
          </button>
        )}

        {showMetadata && d.technology && !compact && (
          <div className="mt-2 flex flex-wrap gap-1">
            {d.technology.split(',').map((tech) => (
              <span
                key={tech.trim()}
                className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground"
              >
                {tech.trim()}
              </span>
            ))}
          </div>
        )}
      </div>
    </div>
  )
})
