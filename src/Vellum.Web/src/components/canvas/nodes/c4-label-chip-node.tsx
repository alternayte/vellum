import { memo } from 'react'
import type { NodeProps } from '@xyflow/react'
import { SideHandles } from './side-handles'
import { elementColor } from '@/lib/kind-colors'
import { cn } from '@/lib/utils'
import { factStatusColor, factStatusLabel, provenanceSummary } from '@/lib/fact-status'
import type { C4ElementNodeType } from './c4-element-node'
import { StressChipDot } from '../stress-overlay'

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
        'group/chip flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 shadow-sm',
        diff === 'removed' && 'opacity-50',
        diff === 'unchanged' && 'opacity-60',
      )}
      style={diffOverrideBorder ? { borderColor: diffOverrideBorder } : undefined}
    >
      <SideHandles className="h-1.5! w-1.5!" hoverClassName="group-hover/chip:opacity-100" />
      <span
        className="h-2 w-2 rounded-full"
        style={{ backgroundColor: elementColor(d.kind, d.tags) }}
      />
      {d.factStatus && d.factStatus !== 'declared' && (
        <span
          role="img"
          aria-label={factStatusLabel(d.factStatus)}
          className="h-1.5 w-1.5 shrink-0 rounded-full"
          style={{ backgroundColor: factStatusColor(d.factStatus) }}
          title={[factStatusLabel(d.factStatus), provenanceSummary(d.provenance)]
            .filter(Boolean)
            .join(' · ')}
        />
      )}
      <span className="text-xs font-medium text-foreground">{d.name}</span>
      <StressChipDot id={d.id} />
      {diff === 'conflict' && <span className="text-[10px]" title="Conflict">⚠</span>}
    </div>
  )
})
