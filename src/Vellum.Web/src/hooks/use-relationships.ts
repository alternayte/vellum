import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdRelationships,
  postApiProjectsByProjectIdRelationships,
  patchApiProjectsByProjectIdRelationshipsByRelationshipId,
  deleteApiProjectsByProjectIdRelationshipsByRelationshipId,
} from '@/api/generated'
import type { UpdateRelationshipRequest } from '@/api/generated/types.gen'
import '@/api/client' // ensure client is configured

export interface Relationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useRelationships(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'relationships'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdRelationships({
        path: { projectId },
        query: { limit: 200 },
      })
      return (result.data as Page<Relationship>).items
    },
    staleTime: 30_000,
  })
}

export function useAddRelationship(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (rel: {
      id: string
      fromId: string
      toId: string
      label?: string
      technology?: string
    }) => {
      const result = await postApiProjectsByProjectIdRelationships({
        path: { projectId },
        body: {
          id: rel.id,
          fromId: rel.fromId,
          toId: rel.toId,
          label: rel.label ?? null,
          technology: rel.technology ?? null,
        },
      })
      return result.data as Relationship
    },
    onMutate: async (newRel) => {
      await queryClient.cancelQueries({ queryKey: ['projects', projectId, 'relationships'] })
      const previous = queryClient.getQueryData<Relationship[]>(['projects', projectId, 'relationships'])
      queryClient.setQueryData<Relationship[]>(
        ['projects', projectId, 'relationships'],
        (old) => [
          ...(old ?? []),
          { ...newRel, label: newRel.label ?? null, technology: newRel.technology ?? null },
        ],
      )
      return { previous }
    },
    onError: (_err, _new, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['projects', projectId, 'relationships'], context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}

export function useUpdateRelationship(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string } & UpdateRelationshipRequest) => {
      const result = await patchApiProjectsByProjectIdRelationshipsByRelationshipId({
        path: { projectId, relationshipId: id },
        body: fields,
      })
      return result.data as Relationship
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}

export function useRemoveRelationship(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdRelationshipsByRelationshipId({
        path: { projectId, relationshipId: id },
      })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}
