import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getApiProjectsByProjectIdElements,
  postApiProjectsByProjectIdElements,
  patchApiProjectsByProjectIdElementsByElementId,
  deleteApiProjectsByProjectIdElementsByElementId,
} from '@/api/generated'
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
  icon: string | null
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useElements(projectId: string, branchId?: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'elements', { branchId }],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdElements({
        path: { projectId },
        query: { limit: 200, branchId },
      })
      return (result.data as Page<Element>).items
    },
    staleTime: 30_000,
  })
}

export function useAddElement(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const cacheKey = ['projects', projectId, 'elements', { branchId: branchId ?? undefined }]

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
        query: { branchId: branchId ?? undefined } as never,
      })
      return result.data as Element
    },
    onMutate: async (newElement) => {
      await queryClient.cancelQueries({ queryKey: cacheKey })
      const previous = queryClient.getQueryData<Element[]>(cacheKey)
      queryClient.setQueryData<Element[]>(
        cacheKey,
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
            icon: null,
          },
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
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
    },
  })
}

export function useUpdateElement(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const cacheKey = ['projects', projectId, 'elements', { branchId: branchId ?? undefined }]

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string } & Partial<Omit<Element, 'id'>>) => {
      const result = await patchApiProjectsByProjectIdElementsByElementId({
        path: { projectId, elementId: id },
        body: {
          name: fields.name ?? null,
          description: fields.description ?? null,
          technology: fields.technology ?? null,
          ownerId: fields.ownerId ?? null,
          parentId: fields.parentId ?? null,
          status: fields.status ?? null,
          tags: fields.tags ?? null,
          icon: fields.icon ?? null,
          setDescription: 'description' in fields,
          setTechnology: 'technology' in fields,
          setOwnerId: 'ownerId' in fields,
          setParentId: 'parentId' in fields,
          setIcon: 'icon' in fields,
        },
        query: { branchId: branchId ?? undefined } as never,
      })
      return result.data as Element
    },
    onMutate: async ({ id, ...fields }) => {
      await queryClient.cancelQueries({ queryKey: cacheKey })
      const previous = queryClient.getQueryData<Element[]>(cacheKey)
      queryClient.setQueryData<Element[]>(
        cacheKey,
        (old) => old?.map((el) => (el.id === id ? { ...el, ...fields } as Element : el)) ?? [],
      )
      return { previous }
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(cacheKey, context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
    },
  })
}

export function useRemoveElement(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const cacheKey = ['projects', projectId, 'elements', { branchId: branchId ?? undefined }]

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdElementsByElementId({
        path: { projectId, elementId: id },
        query: { branchId: branchId ?? undefined } as never,
      })
    },
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: cacheKey })
      const previous = queryClient.getQueryData<Element[]>(cacheKey)
      queryClient.setQueryData<Element[]>(
        cacheKey,
        (old) => old?.filter((el) => el.id !== id) ?? [],
      )
      return { previous }
    },
    onError: (_err, _id, context) => {
      if (context?.previous) {
        queryClient.setQueryData(cacheKey, context.previous)
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'elements'] })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    },
  })
}
