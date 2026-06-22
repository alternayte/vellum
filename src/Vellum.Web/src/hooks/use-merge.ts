// src/Vellum.Web/src/hooks/use-merge.ts
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  postApiProjectsByProjectIdDraftsByDraftIdMergePreview,
  postApiProjectsByProjectIdDraftsByDraftIdMergeExecute,
} from '@/api/generated'
import '@/api/client'

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
}

export interface MergeResolution {
  entityId: string
  resolution: string
}

export function useMergePreview(projectId: string) {
  return useMutation({
    mutationFn: async (draftId: string) => {
      const result = await postApiProjectsByProjectIdDraftsByDraftIdMergePreview({
        path: { projectId, draftId },
      })
      return result.data as MergePreviewResponse
    },
  })
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
      return result.data
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}
