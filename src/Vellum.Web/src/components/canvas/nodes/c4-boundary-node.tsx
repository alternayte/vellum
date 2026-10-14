import { memo } from 'react'
import type { Node, NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'

export interface C4BoundaryData extends Record<string, unknown> {
  kind: string
  name: string
  icon?: string | null
  technology?: string | null
}

export type C4BoundaryNodeType = Node<C4BoundaryData, 'c4-boundary'>

/**
 * The element whose inside the canvas shows, drawn as a frame around its children. It is
 * derived from the level and the children's places; it is not a model element on this level,
 * so it cannot be selected, moved or connected, and clicks pass through to the canvas.
 */
export const C4BoundaryNode = memo(function C4BoundaryNode({ data }: NodeProps<C4BoundaryNodeType>) {
  const color = kindColor(data.kind)
  const Icon = getIconComponent(data.icon || defaultIconForKind(data.kind))
  return (
    <div
      className="pointer-events-none h-full w-full rounded-2xl border-[1.5px]"
      style={{
        borderColor: `color-mix(in oklab, ${color} 55%, transparent)`,
        backgroundColor: `color-mix(in oklab, ${color} 4%, transparent)`,
      }}
    >
      <div className="flex items-center gap-2 px-4 pt-3">
        {Icon && <Icon className="h-4 w-4 shrink-0" style={{ color }} aria-hidden="true" />}
        <span className="font-display truncate text-sm font-semibold text-foreground">{data.name}</span>
        <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{data.kind}</span>
        {data.technology && (
          <span className="truncate text-[11px] text-muted-foreground">· {data.technology}</span>
        )}
      </div>
    </div>
  )
})
