import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useCallback, useEffect } from 'react'
import {
  getApiProjectsByProjectIdViews,
  getApiProjectsByProjectIdViewsByViewId,
  postApiProjectsByProjectIdViews,
  putApiProjectsByProjectIdViewsByViewIdLayout,
} from '@/api/generated'
import '@/api/client' // ensure client is configured

export interface View {
  id: string
  projectId: string
  name: string
  rootElementId: string | null
  visibleElementIds: string[]
  activeLens: string | null
  activeFlowId: string | null
  createdAt: string
  updatedAt: string
}

export interface ViewDetail extends View {
  positions: { elementId: string; x: number; y: number }[]
  edges: { relationshipId: string; routePoints: unknown }[]
}

export function useViews(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'views'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdViews({ path: { projectId } })
      return result.data as View[]
    },
    staleTime: 60_000,
  })
}

export function useView(projectId: string, viewId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'views', viewId],
    queryFn: async () => {
      if (!viewId) return null
      const result = await getApiProjectsByProjectIdViewsByViewId({
        path: { projectId, viewId },
      })
      return result.data as ViewDetail
    },
    enabled: !!viewId,
  })
}

export function useCreateView(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (view: { id: string; name: string; rootElementId?: string }) => {
      const result = await postApiProjectsByProjectIdViews({
        path: { projectId },
        body: { id: view.id, name: view.name, rootElementId: view.rootElementId ?? null },
      })
      return result.data as View
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'views'] })
    },
  })
}

export function useSaveLayout(projectId: string, viewId: string | null) {
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const saveFn = useCallback(
    async (positions: { elementId: string; x: number; y: number }[]) => {
      if (!viewId) return
      await putApiProjectsByProjectIdViewsByViewIdLayout({
        path: { projectId, viewId },
        body: { positions, edges: null },
      })
    },
    [projectId, viewId],
  )

  const debouncedSave = useCallback(
    (positions: { elementId: string; x: number; y: number }[]) => {
      if (timerRef.current) clearTimeout(timerRef.current)
      timerRef.current = setTimeout(() => saveFn(positions), 300)
    },
    [saveFn],
  )

  useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])

  return { saveLayout: debouncedSave }
}
