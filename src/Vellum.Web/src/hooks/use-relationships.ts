import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdRelationships,
  postApiProjectsByProjectIdRelationships,
  patchApiProjectsByProjectIdRelationshipsByRelationshipId,
  deleteApiProjectsByProjectIdRelationshipsByRelationshipId,
} from '@/api/generated'
import '@/api/client' // ensure client is configured

export interface Relationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
  lineShape: string | null
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useRelationships(projectId: string, branchId?: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'relationships', { branchId }],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdRelationships({
        path: { projectId },
        query: { limit: 200, branchId },
      })
      return (result.data as Page<Relationship>).items
    },
    staleTime: 30_000,
  })
}

export function useAddRelationship(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const cacheKey = ['projects', projectId, 'relationships', { branchId: branchId ?? undefined }]

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
        query: { branchId: branchId ?? undefined } as never,
      })
      return result.data as Relationship
    },
    onMutate: async (newRel) => {
      await queryClient.cancelQueries({ queryKey: cacheKey })
      const previous = queryClient.getQueryData<Relationship[]>(cacheKey)
      queryClient.setQueryData<Relationship[]>(
        cacheKey,
        (old) => [
          ...(old ?? []),
          { ...newRel, label: newRel.label ?? null, technology: newRel.technology ?? null, lineShape: null },
        ],
      )
      return { previous }
    },
    onError: (_err, _new, context) => {
      if (context?.previous) {
        queryClient.setQueryData(cacheKey, context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}

export function useUpdateRelationship(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string } & Partial<Omit<Relationship, 'id' | 'fromId' | 'toId'>>) => {
      const result = await patchApiProjectsByProjectIdRelationshipsByRelationshipId({
        path: { projectId, relationshipId: id },
        body: {
          label: fields.label ?? null,
          technology: fields.technology ?? null,
          lineShape: fields.lineShape ?? null,
          setLabel: 'label' in fields,
          setTechnology: 'technology' in fields,
          setLineShape: 'lineShape' in fields,
        },
        query: { branchId: branchId ?? undefined } as never,
      })
      return result.data as Relationship
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}

export function useRemoveRelationship(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdRelationshipsByRelationshipId({
        path: { projectId, relationshipId: id },
        query: { branchId: branchId ?? undefined } as never,
      })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}
