import { memo } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'

export interface C4ContainerData extends Record<string, unknown> {
  id: string
  kind: string
  name: string
}

export type C4ContainerNodeType = Node<C4ContainerData, 'c4-container'>

export const C4ContainerNode = memo(function C4ContainerNode({
  data,
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
      }}
    >
      <Handle type="target" position={Position.Top} className="!h-2 !w-2 !bg-primary opacity-0" />
      <Handle type="source" position={Position.Right} className="!h-2 !w-2 !bg-primary opacity-0" />
      <Handle type="target" position={Position.Bottom} className="!h-2 !w-2 !bg-primary opacity-0" />
      <Handle type="source" position={Position.Left} className="!h-2 !w-2 !bg-primary opacity-0" />

      <div className="mb-2 flex items-center gap-1.5">
        <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">
          {data.kind}
        </span>
        <span className="text-xs font-medium text-muted-foreground">
          {data.name}
        </span>
      </div>
    </div>
  )
})
