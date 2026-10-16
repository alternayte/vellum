import { memo } from 'react'
import { NodeResizer, type Node, type NodeProps } from '@xyflow/react'
import { SideHandles } from './side-handles'
import { Plus } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'

export interface C4ContainerData extends Record<string, unknown> {
  id: string
  kind: string
  name: string
  icon?: string | null
  onResize?: (width: number, height: number) => void
  onAddChild?: () => void
}

export type C4ContainerNodeType = Node<C4ContainerData, 'c4-container'>

export const C4ContainerNode = memo(function C4ContainerNode({
  data,
  selected,
}: NodeProps<C4ContainerNodeType>) {
  const color = kindColor(data.kind)
  const Icon = getIconComponent(data.icon || defaultIconForKind(data.kind))

  return (
    // The same frame as the boundary of a level (C4BoundaryNode), here around an element opened in place.
    <div
      className="rounded-2xl px-4 pt-3"
      style={{
        borderWidth: '1.5px',
        borderStyle: 'solid',
        borderColor: `color-mix(in oklab, ${color} 55%, transparent)`,
        backgroundColor: `color-mix(in oklab, ${color} 4%, transparent)`,
        minWidth: '400px',
        minHeight: '200px',
        width: '100%',
        height: '100%',
      }}
    >
      <NodeResizer
        minWidth={400}
        minHeight={200}
        isVisible={!!selected}
        lineClassName="border-primary!"
        handleClassName="h-2! w-2! rounded-sm! border-primary! bg-background!"
        onResizeEnd={(_event, params) => {
          data.onResize?.(params.width, params.height)
        }}
      />
      <SideHandles className="h-2! w-2!" />

      <div className="mb-2 flex items-center gap-2">
        {Icon && <Icon className="h-4 w-4 shrink-0" style={{ color }} aria-hidden="true" />}
        <span className="font-display truncate text-sm font-semibold text-foreground">{data.name}</span>
        <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{data.kind}</span>
        {data.onAddChild && (
          <button
            type="button"
            aria-label={`Add an element inside ${data.name}`}
            title={`Add an element inside ${data.name}`}
            className="nodrag nopan ml-1 flex h-5 w-5 items-center justify-center rounded border border-border bg-card text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            onClick={(e) => { e.stopPropagation(); data.onAddChild?.() }}
            onDoubleClick={(e) => e.stopPropagation()}
          >
            <Plus className="h-3 w-3" aria-hidden="true" />
          </button>
        )}
      </div>
    </div>
  )
})
