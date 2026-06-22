// src/Vellum.Web/src/hooks/use-spaces.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdSpaces,
  postApiProjectsByProjectIdSpaces,
  patchApiProjectsByProjectIdSpacesBySpaceId,
  deleteApiProjectsByProjectIdSpacesBySpaceId,
} from '@/api/generated'
import '@/api/client'

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
    mutationFn: async (space: { id: string; name: string }) => {
      const result = await postApiProjectsByProjectIdSpaces({
        path: { projectId },
        body: { id: space.id, name: space.name },
      })
      return result.data as Space
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
    },
  })
}

export function useUpdateSpace(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string; name?: string }) => {
      const result = await patchApiProjectsByProjectIdSpacesBySpaceId({
        path: { projectId, spaceId: id },
        body: { name: fields.name ?? null },
      })
      return result.data as Space
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
    },
  })
}

export function useDeleteSpace(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdSpacesBySpaceId({
        path: { projectId, spaceId: id },
      })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'spaces'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
  })
}
