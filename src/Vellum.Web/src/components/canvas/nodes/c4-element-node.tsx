import { memo } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'
import { useCanvasStore } from '@/stores/canvas-store'
import { cn } from '@/lib/utils'
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
  diffState?: DiffState
}

export type C4ElementNodeType = Node<C4ElementData, 'c4-element'>

const diffBorderColor: Record<string, string> = {
  added: '#2F855A',
  modified: '#B7791F',
  removed: '#B5483D',
  conflict: '#B7791F',
}

export const C4ElementNode = memo(function C4ElementNode({ data }: NodeProps<C4ElementNodeType>) {
  const d = data
  const activeLens = useCanvasStore((s) => s.activeLens)
  const showMetadata = activeLens === 'metadata'
  const showStatus = activeLens === 'status'

  const borderStyle = showStatus && d.status === 'planned' ? 'dashed' : 'solid'
  const bgTint = showStatus && d.status === 'deprecated' ? 'hsl(var(--status-deprecated) / 0.1)' : undefined

  const diff = d.diffState
  const diffOverrideBorder = diff && diff !== 'unchanged' ? diffBorderColor[diff] : undefined
  const diffBg = diff === 'added' ? 'rgba(47, 133, 90, 0.05)' : bgTint

  return (
    <div
      className={cn(
        'relative w-[280px] rounded-lg border border-border bg-card shadow-sm',
        diff === 'removed' && 'opacity-50',
        diff === 'unchanged' && 'opacity-60',
      )}
      style={{
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
      <Handle type="target" position={Position.Left} className="!bg-primary" />
      <Handle type="source" position={Position.Right} className="!bg-primary" />

      <div className="p-3">
        <div className="flex items-center justify-between">
          <span className="font-display text-sm font-medium text-foreground">
            {d.name}
          </span>
          <div className="flex items-center gap-1">
            {diff === 'conflict' && (
              <span className="text-[12px]" title="Conflict">⚠</span>
            )}
            <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
              {d.kind}
            </span>
          </div>
        </div>

        {d.description && (
          <p className="mt-1 line-clamp-2 text-xs text-muted-foreground">
            {d.description}
          </p>
        )}

        {showMetadata && d.technology && (
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
