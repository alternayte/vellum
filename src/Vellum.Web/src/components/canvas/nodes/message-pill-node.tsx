import { memo } from 'react'
import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { cn } from '@/lib/utils'

export interface MessagePillData extends Record<string, unknown> {
  id: string
  name: string
  hasSchema: boolean
}

export type MessagePillNodeType = Node<MessagePillData, 'message-pill'>

export const MessagePillNode = memo(function MessagePillNode({
  data,
}: NodeProps<MessagePillNodeType>) {
  return (
    <div
      className={cn(
        'flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 shadow-sm',
        'min-w-[80px] max-w-[200px]',
      )}
    >
      <Handle type="target" position={Position.Left} className="!bg-primary" />
      <Handle type="source" position={Position.Right} className="!bg-primary" />

      <span className="text-[10px] text-muted-foreground">✉</span>
      <span className="truncate font-mono text-xs text-foreground">{data.name}</span>
      {data.hasSchema && (
        <span className="text-[10px] text-muted-foreground" title="Schema attached">
          ⬡
        </span>
      )}
    </div>
  )
})
