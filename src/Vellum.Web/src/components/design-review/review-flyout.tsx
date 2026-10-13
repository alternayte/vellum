import { useCallback, useState } from 'react'
import { cn } from '@/lib/utils'
import { useShellStore } from '@/stores/shell-store'
import type { Cited } from '@/lib/review-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { FindingsTab } from './findings-tab'
import { DecisionsTab } from './decisions-tab'
import { QuestionsTab } from './questions-tab'
import { AssistantTab } from './assistant-tab'

interface ReviewFlyoutProps {
  projectId: string
  branchId?: string | null
  references: ScenarioReference[]
  elementIds: string[]
  /** The element that owns each contract: the owner of an operation, the consumer of a subscription, the producer of a message. */
  owners: Record<string, string>
}

const TABS = [
  { id: 'findings', label: 'Findings' },
  { id: 'decisions', label: 'Decisions' },
  { id: 'questions', label: 'Questions' },
  { id: 'assistant', label: 'Assistant' },
] as const

/**
 * Review mode: what the deterministic rules find in the model, the decisions that answer
 * the findings, the open questions, and the suggestions of the assistant. The findings
 * need no AI.
 */
export function ReviewFlyout({ projectId, branchId, references, elementIds, owners }: ReviewFlyoutProps) {
  const [tab, setTab] = useState<(typeof TABS)[number]['id']>('findings')
  const focusElement = useShellStore((s) => s.focusElement)
  const focusTarget = useCallback(
    (cited: Cited) => (cited.kind === 'element' && elementIds.includes(cited.id) ? cited.id : (owners[cited.id] ?? null)),
    [elementIds, owners],
  )

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Review</span>
      </div>
      <div role="tablist" aria-label="Review" className="flex gap-1 border-b border-border p-2">
        {TABS.map(({ id, label }) => (
          <button
            key={id} type="button" role="tab" id={`review-tab-${id}`} aria-selected={tab === id} aria-controls="review-panel"
            onClick={() => setTab(id)}
            className={cn(
              'flex-1 rounded-md px-2 py-1 text-xs focus-visible:outline-2 focus-visible:outline-primary',
              tab === id ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground',
            )}
          >
            {label}
          </button>
        ))}
      </div>
      <div role="tabpanel" id="review-panel" aria-labelledby={`review-tab-${tab}`} className="min-h-0 flex-1 overflow-y-auto">
        {tab === 'findings' && <FindingsTab projectId={projectId} branchId={branchId} focusTarget={focusTarget} onFocus={focusElement} />}
        {tab === 'decisions' && <DecisionsTab projectId={projectId} branchId={branchId} references={references} />}
        {tab === 'questions' && <QuestionsTab projectId={projectId} />}
        {tab === 'assistant' && <AssistantTab projectId={projectId} branchId={branchId} focusTarget={focusTarget} onFocus={focusElement} />}
      </div>
    </div>
  )
}
