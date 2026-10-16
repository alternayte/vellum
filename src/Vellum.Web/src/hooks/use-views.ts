import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useRef, useCallback, useEffect } from 'react'
import { toast } from 'sonner'
import {
  getApiProjectsByProjectIdViews,
  getApiProjectsByProjectIdViewsByViewId,
  postApiProjectsByProjectIdViews,
  putApiProjectsByProjectIdViewsByViewIdLayout,
  putApiProjectsByProjectIdViewsByViewIdLayoutEdgesByRelationshipId,
  patchApiProjectsByProjectIdViewsByViewId,
  deleteApiProjectsByProjectIdViewsByViewId,
} from '@/api/generated'
import '@/api/client' // ensure client is configured
import { unwrap } from '@/hooks/use-contracts'
import { shownInline } from '@/lib/mutation-errors'

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
  positions: { elementId: string; x: number; y: number; width?: number; height?: number }[]
  /** The saved route and sides of each relationship that has one in this view. A null side is auto. */
  edges: { relationshipId: string; routePoints: unknown; sourceSide?: string | null; targetSide?: string | null }[]
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

export function useRenameView(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    // The screen that runs it shows the failure next to its control.
    onError: shownInline,
    mutationFn: async ({ id, name }: { id: string; name: string }) => {
      const result = await patchApiProjectsByProjectIdViewsByViewId({
        path: { projectId, viewId: id },
        body: { name, rootElementId: null, visibleElementIds: null, activeLens: null, updatedAt: null },
      })
      return unwrap<View>(result, 'The diagram was not renamed.')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'views'] })
    },
  })
}

/** Deletes a diagram. A document that embeds it keeps its text; the embed says the diagram was deleted. */
export function useDeleteView(projectId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    // The screen that runs it shows the failure next to its control.
    onError: shownInline,
    mutationFn: async (id: string) => {
      unwrap(await deleteApiProjectsByProjectIdViewsByViewId({ path: { projectId, viewId: id } }), 'The diagram was not deleted.')
    },
    onSuccess: (_, id) => {
      queryClient.removeQueries({ queryKey: ['projects', projectId, 'views', id] })
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'views'] })
    },
  })
}

export function useSaveLayout(projectId: string, viewId: string | null) {
  const queryClient = useQueryClient()
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  const saveFn = useCallback(
    async (positions: { elementId: string; x: number; y: number; width?: number; height?: number }[]) => {
      if (!viewId) return
      await putApiProjectsByProjectIdViewsByViewIdLayout({
        path: { projectId, viewId },
        body: { positions, edges: null },
      })
    },
    [projectId, viewId],
  )

  const debouncedSave = useCallback(
    (positions: { elementId: string; x: number; y: number; width?: number; height?: number }[]) => {
      if (viewId) {
        queryClient.setQueryData<ViewDetail>(
          ['projects', projectId, 'views', viewId],
          (old) => old ? { ...old, positions } : old,
        )
      }
      if (timerRef.current) clearTimeout(timerRef.current)
      timerRef.current = setTimeout(() => saveFn(positions), 300)
    },
    [saveFn, viewId, projectId, queryClient],
  )

  useEffect(() => () => { if (timerRef.current) clearTimeout(timerRef.current) }, [])

  return { saveLayout: debouncedSave }
}

/**
 * Saves the sides of one relationship in a view: top, right, bottom or left, or null for auto.
 * The canvas shows the new sides at once; a failed save puts the saved ones back.
 */
export function useSaveEdgeSides(projectId: string, viewId: string | null) {
  const queryClient = useQueryClient()
  const key = ['projects', projectId, 'views', viewId]

  return useMutation({
    mutationFn: async ({ relationshipId, sourceSide, targetSide }: { relationshipId: string; sourceSide: string | null; targetSide: string | null }) => {
      if (!viewId) throw new Error('No diagram is open.')
      unwrap(
        await putApiProjectsByProjectIdViewsByViewIdLayoutEdgesByRelationshipId({
          path: { projectId, viewId, relationshipId },
          body: { sourceSide, targetSide },
        }),
        'The sides of the relationship were not saved.',
      )
    },
    onMutate: async ({ relationshipId, sourceSide, targetSide }) => {
      await queryClient.cancelQueries({ queryKey: key })
      const previous = queryClient.getQueryData<ViewDetail>(key)
      queryClient.setQueryData<ViewDetail>(key, (old) => {
        if (!old) return old
        const rest = old.edges.filter((e) => e.relationshipId !== relationshipId)
        const current = old.edges.find((e) => e.relationshipId === relationshipId)
        const routePoints = current?.routePoints ?? null
        const keep = sourceSide !== null || targetSide !== null || routePoints !== null
        return { ...old, edges: keep ? [...rest, { relationshipId, routePoints, sourceSide, targetSide }] : rest }
      })
      return { previous }
    },
    onError: (error, _vars, context) => {
      if (context?.previous) queryClient.setQueryData(key, context.previous)
      toast.error(error.message)
    },
  })
}
