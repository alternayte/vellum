import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'
import type { C4ElementData } from './c4-element-node'

export const C4LabelChipNode = memo(function C4LabelChipNode({ data }: NodeProps) {
  const d = data as C4ElementData

  return (
    <div className="flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 shadow-sm">
      <Handle type="target" position={Position.Left} className="!bg-primary" />
      <Handle type="source" position={Position.Right} className="!bg-primary" />
      <span
        className="h-2 w-2 rounded-full"
        style={{ backgroundColor: kindColor(d.kind) }}
      />
      <span className="text-xs font-medium text-foreground">{d.name}</span>
    </div>
  )
})
