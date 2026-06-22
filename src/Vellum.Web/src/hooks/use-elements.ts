import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdElements,
  postApiProjectsByProjectIdElements,
  patchApiProjectsByProjectIdElementsByElementId,
  deleteApiProjectsByProjectIdElementsByElementId,
} from '@/api/generated'
import type { UpdateElementRequest } from '@/api/generated/types.gen'
import '@/api/client' // ensure client is configured

export interface Element {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  ownerId: string | null
  status: string
  parentId: string | null
  tags: string[]
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useElements(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'elements'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdElements({
        path: { projectId },
        query: { limit: 200 },
      })
      return (result.data as Page<Element>).items
    },
    staleTime: 30_000,
  })
}

export function useAddElement(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (element: {
      id: string
      kind: string
      name: string
      description?: string
      technology?: string
      parentId?: string
      status?: string
      tags?: string[]
    }) => {
      const result = await postApiProjectsByProjectIdElements({
        path: { projectId },
        body: {
          id: element.id,
          kind: element.kind,
          name: element.name,
          description: element.description ?? null,
          technology: element.technology ?? null,
          ownerId: null,
          parentId: element.parentId ?? null,
          status: element.status ?? 'current',
          tags: element.tags ?? [],
        },
      })
      return result.data as Element
    },
    onMutate: async (newElement) => {
      await queryClient.cancelQueries({ queryKey: ['projects', projectId, 'elements'] })
      const previous = queryClient.getQueryData<Element[]>(['projects', projectId, 'elements'])
      queryClient.setQueryData<Element[]>(
        ['projects', projectId, 'elements'],
        (old) => [
          ...(old ?? []),
          {
            ...newElement,
            description: newElement.description ?? null,
            technology: newElement.technology ?? null,
            ownerId: null,
            parentId: newElement.parentId ?? null,
            status: newElement.status ?? 'current',
            tags: newElement.tags ?? [],
          },
        ],
      )
      return { previous }
    },
    onError: (_err, _new, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['projects', projectId, 'elements'], context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
    },
  })
}

export function useUpdateElement(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string } & UpdateElementRequest) => {
      const result = await patchApiProjectsByProjectIdElementsByElementId({
        path: { projectId, elementId: id },
        body: fields,
      })
      return result.data as Element
    },
    onMutate: async ({ id, ...fields }) => {
      await queryClient.cancelQueries({ queryKey: ['projects', projectId, 'elements'] })
      const previous = queryClient.getQueryData<Element[]>(['projects', projectId, 'elements'])
      queryClient.setQueryData<Element[]>(
        ['projects', projectId, 'elements'],
        (old) => old?.map((el) => (el.id === id ? { ...el, ...fields } as Element : el)) ?? [],
      )
      return { previous }
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['projects', projectId, 'elements'], context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
    },
  })
}

export function useRemoveElement(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdElementsByElementId({
        path: { projectId, elementId: id },
      })
    },
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: ['projects', projectId, 'elements'] })
      const previous = queryClient.getQueryData<Element[]>(['projects', projectId, 'elements'])
      queryClient.setQueryData<Element[]>(
        ['projects', projectId, 'elements'],
        (old) => old?.filter((el) => el.id !== id) ?? [],
      )
      return { previous }
    },
    onError: (_err, _id, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['projects', projectId, 'elements'], context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}
