// src/Vellum.Web/src/hooks/use-spaces.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdSpaces,
  postApiProjectsByProjectIdSpaces,
  patchApiProjectsByProjectIdSpacesBySpaceId,
  deleteApiProjectsByProjectIdSpacesBySpaceId,
} from '@/api/generated'
import '@/api/client'
import { unwrap } from '@/hooks/use-contracts'
import { shownInline } from '@/lib/mutation-errors'

export interface Space {
  id: string
  projectId: string
  name: string
  createdAt: string
  updatedAt: string
}

export function useSpaces(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'spaces'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdSpaces({ path: { projectId } })
      return result.data as Space[]
    },
    staleTime: 60_000,
  })
}

export function useCreateSpace(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    // The screen that runs it shows the failure next to its control.
    onError: shownInline,
    mutationFn: async (space: { id: string; name: string }) => {
      const result = await postApiProjectsByProjectIdSpaces({
        path: { projectId },
        body: { id: space.id, name: space.name },
      })
      return unwrap<Space>(result, 'The space was not created.')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
    },
  })
}

export function useUpdateSpace(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    // The screen that runs it shows the failure next to its control.
    onError: shownInline,
    mutationFn: async ({ id, ...fields }: { id: string; name?: string }) => {
      const result = await patchApiProjectsByProjectIdSpacesBySpaceId({
        path: { projectId, spaceId: id },
        body: { name: fields.name ?? null },
      })
      return unwrap<Space>(result, 'The space was not renamed.')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
    },
  })
}

export function useDeleteSpace(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    // The screen that runs it shows the failure next to its control.
    onError: shownInline,
    mutationFn: async (id: string) => {
      unwrap(await deleteApiProjectsByProjectIdSpacesBySpaceId({
        path: { projectId, spaceId: id },
      }), 'The space was not deleted.')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
  })
}
