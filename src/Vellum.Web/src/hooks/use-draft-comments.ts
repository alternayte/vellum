// src/Vellum.Web/src/hooks/use-draft-comments.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdDraftsByDraftIdComments,
  postApiProjectsByProjectIdDraftsByDraftIdComments,
  patchApiProjectsByProjectIdDraftsByDraftIdCommentsByCommentId,
  deleteApiProjectsByProjectIdDraftsByDraftIdCommentsByCommentId,
} from '@/api/generated'
import '@/api/client'

export interface CommentDto {
  id: string
  draftId: string
  entityId: string | null
  entityType: string | null
  author: string
  body: string
  createdAt: string
  updatedAt: string
}

interface CommentPage {
  items: CommentDto[]
  cursor: string | null
}

export function useComments(projectId: string, draftId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'drafts', draftId, 'comments'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdDraftsByDraftIdComments({
        path: { projectId, draftId: draftId! },
      })
      return result.data as CommentPage
    },
    enabled: !!draftId,
    staleTime: 30_000,
  })
}

export function useCreateComment(projectId: string, draftId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (data: { id: string; body: string; entityId?: string | null; entityType?: string | null }) => {
      const result = await postApiProjectsByProjectIdDraftsByDraftIdComments({
        path: { projectId, draftId },
        body: {
          id: data.id,
          body: data.body,
          entityId: data.entityId ?? null,
          entityType: data.entityType ?? null,
        },
      })
      return result.data as CommentDto
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts', draftId, 'comments'] })
    },
  })
}

export function useUpdateComment(projectId: string, draftId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ commentId, body }: { commentId: string; body: string }) => {
      const result = await patchApiProjectsByProjectIdDraftsByDraftIdCommentsByCommentId({
        path: { projectId, draftId, commentId },
        body: { body },
      })
      return result.data as CommentDto
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts', draftId, 'comments'] })
    },
  })
}

export function useDeleteComment(projectId: string, draftId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (commentId: string) => {
      await deleteApiProjectsByProjectIdDraftsByDraftIdCommentsByCommentId({
        path: { projectId, draftId, commentId },
      })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'drafts', draftId, 'comments'] })
    },
  })
}
