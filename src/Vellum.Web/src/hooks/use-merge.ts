// src/Vellum.Web/src/hooks/use-merge.ts
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { postApiProjectsByProjectIdDraftsByDraftIdMergeExecute } from '@/api/generated'
import '@/api/client'
import { shownInline } from '@/lib/mutation-errors'

export interface MergeChange {
  entityType: string
  entityId: string
  changeKind: string
  resolvedValue: unknown
}

export interface MergeConflict {
  entityType: string
  entityId: string
  conflictKind: string
  baseValue: unknown
  oursValue: unknown
  theirsValue: unknown
}

export interface MergePreviewResponse {
  autoResolved: MergeChange[]
  conflicts: MergeConflict[]
  mainVersion: number
  /** What the merge does to the cost sheet of main. The server refuses the merge while `conflicts` is not empty. */
  costSheet: { changes: number; conflicts: string[] }
}

export interface MergeResolution {
  entityId: string
  resolution: string
}

export function useExecuteMerge(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({
      draftId,
      resolutions,
      expectedMainVersion,
    }: {
      draftId: string
      resolutions: MergeResolution[]
      expectedMainVersion: number
    }) => {
      const result = await postApiProjectsByProjectIdDraftsByDraftIdMergeExecute({
        path: { projectId, draftId },
        body: { resolutions, expectedMainVersion },
      })
      // The generated client returns a refusal; it does not throw. A refused merge wrote nothing.
      if (result.error !== undefined || !result.response?.ok) {
        const refusal = (result.error ?? {}) as { title?: string; detail?: string | null }
        throw new Error(refusal.title ? (refusal.detail ? `${refusal.title}. ${refusal.detail}` : refusal.title) : 'The merge did not complete.')
      }
      return result.data
    },
    // The review bar shows the reason next to the Merge button.
    onError: shownInline,
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'costs'] })
    },
  })
}
