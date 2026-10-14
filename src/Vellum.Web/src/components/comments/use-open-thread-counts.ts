import { useMemo } from 'react'
import { openThreadCounts, targetKey, useThreads, type TargetKind } from '@/hooks/use-threads'
import { useCommentAccess } from './use-comment-access'

/**
 * The open threads of each target of the shown branch, by `targetKey`. A list of promises,
 * decisions, documents or steps reads its counts from here.
 */
export function useOpenThreadCounts(projectId: string): (kind: TargetKind, id: string, stepId?: string | null) => number {
  const { draftId } = useCommentAccess(projectId)
  const { data: threads } = useThreads(projectId, draftId)
  const counts = useMemo(() => openThreadCounts(threads), [threads])
  return (kind, id, stepId) => counts[targetKey(kind, id, stepId)] ?? 0
}

/** The open threads on all the steps of each scenario, by scenario ID, for the scenario list. */
export function useOpenStepThreadCounts(projectId: string): (scenarioId: string) => number {
  const { draftId } = useCommentAccess(projectId)
  const { data: threads } = useThreads(projectId, draftId)
  const counts = useMemo(() => {
    const byScenario: Record<string, number> = {}
    for (const t of threads ?? []) {
      if (t.status === 'open' && t.target.kind === 'step') byScenario[t.target.id] = (byScenario[t.target.id] ?? 0) + 1
    }
    return byScenario
  }, [threads])
  return (scenarioId) => counts[scenarioId] ?? 0
}
