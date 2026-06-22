import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'
import { useCanvasStore } from '@/stores/canvas-store'

export interface C4ElementData {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  tags: string[]
  ownerId: string | null
}

export const C4ElementNode = memo(function C4ElementNode({ data }: NodeProps) {
  const d = data as C4ElementData
  const activeLens = useCanvasStore((s) => s.activeLens)
  const showMetadata = activeLens === 'metadata'
  const showStatus = activeLens === 'status'

  const borderStyle = showStatus && d.status === 'planned' ? 'dashed' : 'solid'
  const bgTint = showStatus && d.status === 'deprecated' ? 'hsl(var(--status-deprecated) / 0.1)' : undefined

  return (
    <div
      className="relative w-[280px] rounded-lg border border-border bg-card shadow-sm"
      style={{
        borderLeftWidth: '4px',
        borderLeftColor: kindColor(d.kind),
        borderLeftStyle: 'solid',
        borderTopStyle: borderStyle,
        borderRightStyle: borderStyle,
        borderBottomStyle: borderStyle,
        backgroundColor: bgTint,
      }}
    >
      <Handle type="target" position={Position.Left} className="!bg-primary" />
      <Handle type="source" position={Position.Right} className="!bg-primary" />

      <div className="p-3">
        <div className="flex items-center justify-between">
          <span className="font-display text-sm font-medium text-foreground">
            {d.name}
          </span>
          <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
            {d.kind}
          </span>
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
