// src/Vellum.Web/src/hooks/use-docs.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useCallback, useEffect } from 'react'
import {
  getApiProjectsByProjectIdDocs,
  getApiProjectsByProjectIdDocsByDocId,
  postApiProjectsByProjectIdDocs,
  patchApiProjectsByProjectIdDocsByDocId,
  deleteApiProjectsByProjectIdDocsByDocId,
} from '@/api/generated'
import '@/api/client'

export interface Doc {
  id: string
  projectId: string
  spaceId: string | null
  elementId: string | null
  draftId: string | null
  type: string | null
  title: string
  content: string
  createdBy: string
  createdAt: string
  updatedAt: string
}

interface DocPage {
  items: Doc[]
  cursor: string | null
}

export function useDocs(
  projectId: string,
  filters?: { spaceId?: string; elementId?: string; draftId?: string; type?: string },
) {
  return useQuery({
    queryKey: ['projects', projectId, 'docs', filters],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdDocs({
        path: { projectId },
        query: {
          limit: 200,
          spaceId: filters?.spaceId,
          elementId: filters?.elementId,
          draftId: filters?.draftId,
          type: filters?.type,
        },
      })
      return (result.data as DocPage).items
    },
    staleTime: 30_000,
  })
}

export function useDoc(projectId: string, docId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'docs', docId],
    queryFn: async () => {
      if (!docId) return null
      const result = await getApiProjectsByProjectIdDocsByDocId({
        path: { projectId, docId },
      })
      return result.data as Doc
    },
    enabled: !!docId,
  })
}

export function useCreateDoc(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (doc: {
      id: string
      title: string
      spaceId?: string
      elementId?: string
      draftId?: string
      type?: string
    }) => {
      const result = await postApiProjectsByProjectIdDocs({
        path: { projectId },
        body: {
          id: doc.id,
          title: doc.title,
          spaceId: doc.spaceId ?? null,
          elementId: doc.elementId ?? null,
          draftId: doc.draftId ?? null,
          type: doc.type ?? null,
        },
      })
      return result.data as Doc
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
  })
}

export function useUpdateDoc(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async ({ id, ...fields }: { id: string } & Partial<Pick<Doc, 'title' | 'content' | 'spaceId' | 'elementId'>>) => {
      const result = await patchApiProjectsByProjectIdDocsByDocId({
        path: { projectId, docId: id },
        body: {
          title: fields.title ?? null,
          content: fields.content ?? null,
          spaceId: fields.spaceId ?? null,
          elementId: fields.elementId ?? null,
          setSpaceId: 'spaceId' in fields,
          setElementId: 'elementId' in fields,
        },
      })
      return result.data as Doc
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
  })
}

export function useDeleteDoc(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (id: string) => {
      await deleteApiProjectsByProjectIdDocsByDocId({
        path: { projectId, docId: id },
      })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
  })
}

export function useAutoSaveDoc(projectId: string, docId: string | null) {
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const queryClient = useQueryClient()

  const saveFn = useCallback(
    async (content: string) => {
      if (!docId) return
      await patchApiProjectsByProjectIdDocsByDocId({
        path: { projectId, docId },
        body: { title: null, content, spaceId: null, elementId: null, setSpaceId: false, setElementId: false },
      })
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs'] })
    },
    [docId, projectId, queryClient],
  )

  const autoSave = useCallback(
    (content: string) => {
      if (timerRef.current) clearTimeout(timerRef.current)
      timerRef.current = setTimeout(() => saveFn(content), 300)
    },
    [saveFn],
  )

  useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])

  return { autoSave }
}
