import { useEffect, useMemo, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { importApi, needsYouApi, needsYouKey, type NeedsYouSource, type Proposal } from '@/lib/import-api'
import { shownInline } from '@/lib/mutation-errors'
import { useSources } from '@/hooks/use-sources'

/** "Needs you": per Source, what its newest scan did not apply by itself. */
export function useNeedsYou(projectId: string) {
  return useQuery({
    queryKey: needsYouKey(projectId),
    queryFn: () => needsYouApi.list(projectId),
    staleTime: 5000,
  })
}

/** One item of "Needs you" with the Source and the scan it is decided on. */
export interface NeedsYouItem {
  source: NeedsYouSource
  proposal: Proposal
}

/** The items of "Needs you" by the ID of the entity each is about. */
export function useNeedsYouByEntity(projectId: string): Map<string, NeedsYouItem[]> {
  const { data } = useNeedsYou(projectId)
  return useMemo(() => {
    const map = new Map<string, NeedsYouItem[]>()
    for (const source of data ?? []) {
      for (const proposal of source.items) {
        // An addition names an entity that does not exist yet: no card shows it.
        if (proposal.action === 'add') continue
        map.set(proposal.entityId, [...(map.get(proposal.entityId) ?? []), { source, proposal }])
      }
    }
    return map
  }, [data])
}

/**
 * Refreshes the model and "Needs you" when a scan of a Source completes: a completed scan
 * applied what it observed, so the canvas has new facts and new decisions may wait.
 */
export function useScanCompletion(projectId: string) {
  const { data: sources } = useSources(projectId)
  const queryClient = useQueryClient()
  const seen = useRef<Map<string, string> | null>(null)
  useEffect(() => {
    if (!sources) return
    // The first list is what the page loaded with; only what changes after it is news.
    const first = seen.current === null
    const known = (seen.current ??= new Map())
    let completed = false
    for (const source of sources) {
      const scan = source.latestScan
      if (!scan) continue
      const state = `${scan.id}:${scan.status}`
      if (!first && known.get(source.id) !== state && scan.status === 'completed') completed = true
      known.set(source.id, state)
    }
    if (completed) queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
  }, [sources, projectId, queryClient])
}

/** What one decision of "Needs you" sends. */
export interface NeedsYouDecision {
  accept?: string[]
  reject?: string[]
  relink?: { id: string; factKey: string | null }[]
  bind?: { id: string; same: boolean }[]
  acknowledgeImpact?: boolean
}

/** Decides items of "Needs you" on the scan of their Source, then refreshes the model and the list. */
export function useNeedsYouDecision(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ scanId, decision }: { scanId: string; decision: NeedsYouDecision }) =>
      importApi.decide(projectId, scanId, {
        accept: decision.accept ?? [], reject: decision.reject ?? [], relink: decision.relink ?? [],
        bind: decision.bind ?? [], acknowledgeImpact: decision.acknowledgeImpact ?? false,
      }),
    // A decision changes the model, the Sources and the list: every query of the project is stale.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId] }),
    onError: shownInline,
  })
}

/** Keeps a fact that the latest Snapshot no longer holds. */
export function useKeepFact(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ entityKind, entityId }: { entityKind: string; entityId: string }) => needsYouApi.keep(projectId, entityKind, entityId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId] }),
    onError: shownInline,
  })
}
