// src/Vellum.Web/src/hooks/use-drafts.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdDrafts,
  getApiProjectsByProjectIdDraftsByDraftId,
  postApiProjectsByProjectIdDrafts,
  postApiProjectsByProjectIdDraftsByDraftIdAbandon,
} from '@/api/generated'
import '@/api/client'

export interface DraftDto {
  id: string
  projectId: string
  name: string
  streamId: string
  baseStreamId: string
  forkVersion: number
  status: string
  createdBy: string
  createdAt: string
  mergedAt: string | null
  abandonedAt: string | null
}

interface DraftPage {
  items: DraftDto[]
  cursor: string | null
}

export function useDrafts(projectId: string, status?: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'drafts', { status }],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdDrafts({
        path: { projectId },
        query: { status },
      })
      return result.data as DraftPage
    },
    staleTime: 30_000,
  })
}

export function useDraft(projectId: string, draftId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'drafts', draftId],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdDraftsByDraftId({
        path: { projectId, draftId: draftId! },
      })
      return result.data as DraftDto
    },
    enabled: !!draftId,
  })
}

export function useCreateDraft(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (data: { id: string; name: string }) => {
      const result = await postApiProjectsByProjectIdDrafts({
        path: { projectId },
        body: data,
      })
      return result.data as DraftDto
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts'] })
    },
  })
}

export function useAbandonDraft(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (draftId: string) => {
      const result = await postApiProjectsByProjectIdDraftsByDraftIdAbandon({
        path: { projectId, draftId },
      })
      return result.data as DraftDto
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts'] })
    },
  })
}
