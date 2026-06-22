import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { kindColor } from '@/lib/kind-colors'
import { cn } from '@/lib/utils'
import type { C4ElementNodeType } from './c4-element-node'

const diffBorderColor: Record<string, string> = {
  added: '#2F855A',
  modified: '#B7791F',
  removed: '#B5483D',
  conflict: '#B7791F',
}

export const C4LabelChipNode = memo(function C4LabelChipNode({ data }: NodeProps<C4ElementNodeType>) {
  const d = data
  const diff = d.diffState
  const diffOverrideBorder = diff && diff !== 'unchanged' ? diffBorderColor[diff] : undefined

  return (
    <div
      className={cn(
        'flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 shadow-sm',
        diff === 'removed' && 'opacity-50',
        diff === 'unchanged' && 'opacity-60',
      )}
      style={diffOverrideBorder ? { borderColor: diffOverrideBorder } : undefined}
    >
      <Handle type="target" position={Position.Left} className="!bg-primary" />
      <Handle type="source" position={Position.Right} className="!bg-primary" />
      <span
        className="h-2 w-2 rounded-full"
        style={{ backgroundColor: kindColor(d.kind) }}
      />
      <span className="text-xs font-medium text-foreground">{d.name}</span>
      {diff === 'conflict' && <span className="text-[10px]" title="Conflict">⚠</span>}
    </div>
  )
})
