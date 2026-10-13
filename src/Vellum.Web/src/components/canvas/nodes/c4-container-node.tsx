import { memo } from 'react'
import { Handle, Position, NodeResizer, type Node, type NodeProps } from '@xyflow/react'
import { Plus } from 'lucide-react'
import { kindColor } from '@/lib/kind-colors'

export interface C4ContainerData extends Record<string, unknown> {
  id: string
  kind: string
  name: string
  onResize?: (width: number, height: number) => void
  onAddChild?: () => void
}

export type C4ContainerNodeType = Node<C4ContainerData, 'c4-container'>

export const C4ContainerNode = memo(function C4ContainerNode({
  data,
  selected,
}: NodeProps<C4ContainerNodeType>) {
  const color = kindColor(data.kind)

  return (
    <div
      className="rounded-xl bg-card/5 p-4"
      style={{
        borderWidth: '2px',
        borderStyle: 'dashed',
        borderColor: color,
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
      <Handle type="target" position={Position.Top} className="h-2! w-2! bg-primary! opacity-0" />
      <Handle type="source" position={Position.Right} className="h-2! w-2! bg-primary! opacity-0" />
      <Handle type="target" position={Position.Bottom} className="h-2! w-2! bg-primary! opacity-0" />
      <Handle type="source" position={Position.Left} className="h-2! w-2! bg-primary! opacity-0" />

      <div className="mb-2 flex items-center gap-1.5">
        <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
          {data.kind}
        </span>
        <span className="text-xs font-medium text-muted-foreground">
          {data.name}
        </span>
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
