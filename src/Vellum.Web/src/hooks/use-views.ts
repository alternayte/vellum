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
import type { LineRoute } from '@/lib/layout'

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
  /** The saved route of each derived line that has one in this view, by its two cards. */
  lines: LayoutLine[]
}

/** The route of the derived line from one card to another. A route that does not parse draws nothing. */
export interface LayoutLine {
  fromId: string
  toId: string
  route: unknown
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

type SavedPosition = ViewDetail['positions'][number]
type SavedEdge = ViewDetail['edges'][number]

/**
 * The rows of a layout that a whole layout (Tidy) writes: each relationship keeps its saved
 * sides, and a relationship without a saved side takes the route of the layout. A side a person
 * chose wins over the layout, so a relationship with a side keeps no route.
 */
export function layoutRows(saved: SavedEdge[], routes: LineRoute[]): { edges: SavedEdge[]; lines: LayoutLine[] } {
  const byRelationship = new Map(routes.filter((r) => r.relationshipId).map((r) => [r.relationshipId!, r.route]))
  const edges: SavedEdge[] = saved.filter((e) => e.sourceSide || e.targetSide).map((e) => ({ ...e, routePoints: null }))
  const sided = new Set(edges.map((e) => e.relationshipId))
  for (const [relationshipId, route] of byRelationship) {
    if (!sided.has(relationshipId)) edges.push({ relationshipId, routePoints: route, sourceSide: null, targetSide: null })
  }
  const lines = routes.filter((r) => !r.relationshipId).map((r) => ({ fromId: r.fromId, toId: r.toId, route: r.route }))
  return { edges, lines }
}

/**
 * Saves the places of the cards of the open view, after a short pause. With `routes` (from a
 * whole layout) it saves the routes of the lines too; without, the saved routes stay, and a
 * route whose cards moved falls back on the canvas.
 */
export function useSaveLayout(projectId: string, viewId: string | null) {
  const queryClient = useQueryClient()
  const timerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  // The routes of a layout not yet written: a save of places in the pause after it keeps them.
  const pendingRowsRef = useRef<{ edges: SavedEdge[]; lines: LayoutLine[] } | null>(null)

  const saveFn = useCallback(
    async (positions: SavedPosition[], rows: { edges: SavedEdge[]; lines: LayoutLine[] } | null) => {
      if (!viewId) return
      await putApiProjectsByProjectIdViewsByViewIdLayout({
        path: { projectId, viewId },
        body: { positions, edges: rows?.edges ?? null, lines: rows?.lines ?? null },
      })
    },
    [projectId, viewId],
  )

  const debouncedSave = useCallback(
    (positions: SavedPosition[], routes?: LineRoute[]) => {
      if (viewId) {
        const key = ['projects', projectId, 'views', viewId]
        const rows = routes ? layoutRows(queryClient.getQueryData<ViewDetail>(key)?.edges ?? [], routes) : null
        if (rows) pendingRowsRef.current = rows
        queryClient.setQueryData<ViewDetail>(key, (old) => (old ? { ...old, positions, ...(rows ?? {}) } : old))
      }
      if (timerRef.current) clearTimeout(timerRef.current)
      timerRef.current = setTimeout(() => {
        const rows = pendingRowsRef.current
        pendingRowsRef.current = null
        void saveFn(positions, rows)
      }, 300)
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
