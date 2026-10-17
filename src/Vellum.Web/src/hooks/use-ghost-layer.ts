import { useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { designApi, refusedItems, type ArchitectureProposal } from '@/lib/design-api'
import { rejectClosure, withDependants, withDependencies } from '@/lib/ghost-layer'
import { useCanvasStore } from '@/stores/canvas-store'
import { useGhostStore, type GhostRefusal, type ViewPlace } from '@/stores/ghost-store'

/**
 * The decisions of the ghost layer. Each one is one request to the decide endpoint of the
 * proposal; the server applies every accepted item in the order of what it depends on, or
 * nothing. An accepted element keeps the place of its ghost in the open view.
 */
export function useGhostDecisions() {
  const queryClient = useQueryClient()

  const decide = useCallback(async (accept: string[], reject: string[]) => {
    const store = useGhostStore.getState()
    const { projectId, proposal } = store
    if (!projectId || !proposal || store.busy || accept.length + reject.length === 0) return
    store.setBusy(true)
    store.setError(null)
    try {
      const next = await designApi.decideArchitecture(projectId, proposal.id, accept, reject)
      keepPlaces(proposal, next)
      useGhostStore.getState().update(next)
      useGhostStore.getState().setRefusal(null)
      // The model and the lists of proposals read again. The views do not: a read of the open
      // view now would return it without the places just saved for the accepted items.
      await queryClient.invalidateQueries({ predicate: (query) => query.queryKey[0] === 'projects' && query.queryKey[1] === projectId && query.queryKey[2] !== 'views' })
    } catch (error) {
      const failures = refusedItems(error)
      if (failures) {
        const reasons = new Map<string, string[]>()
        for (const failure of failures) reasons.set(failure.itemId, [...(reasons.get(failure.itemId) ?? []), failure.reason])
        useGhostStore.getState().setRefusal({ accept, reasons })
      } else {
        useGhostStore.getState().setError(error instanceof Error ? error.message : 'The decision was not saved.')
      }
    } finally {
      useGhostStore.getState().setBusy(false)
    }
  }, [queryClient])

  const items = () => useGhostStore.getState().proposal?.items ?? []
  const pendingIds = () => items().filter((item) => item.status === 'pending').map((item) => item.id)

  return {
    /** Accepts the item and every pending item it depends on. */
    accept: (itemId: string) => decide([...withDependencies([itemId], items())], []),
    /** Rejects the item and every pending item that needs it. */
    reject: (itemId: string) => decide([], [...rejectClosure([itemId], items())]),
    acceptAll: () => decide(pendingIds(), []),
    rejectAll: () => decide([], pendingIds()),
    /** Accepts what the refused request asked for, without the refused items and what needs them. They stay pending. */
    acceptRest: (refusal: GhostRefusal) => decide(acceptableRest(refusal, items()), []),
    /** Rejects the refused items and what needs them. The rest stays pending. */
    rejectRefused: (refusal: GhostRefusal) => decide([], [...rejectClosure(refusal.reasons.keys(), items())]),
  }
}

export function acceptableRest(refusal: GhostRefusal, items: ArchitectureProposal['items']): string[] {
  const excluded = withDependants(refusal.reasons.keys(), items)
  const pending = new Set(items.filter((item) => item.status === 'pending').map((item) => item.id))
  return refusal.accept.filter((id) => !excluded.has(id) && pending.has(id))
}

/**
 * Saves the place of each ghost element that the decision accepted, so the new card shows
 * where its ghost was. An element that held ghosts stays open on the canvas, with its size.
 */
function keepPlaces(before: ArchitectureProposal, after: ArchitectureProposal) {
  const { places, hostSizes, unsaved, saveViewPlaces } = useGhostStore.getState()
  const wasPending = new Set(before.items.filter((item) => item.status === 'pending').map((item) => item.id))
  const accepted = after.items.filter((item) => item.status === 'accepted' && wasPending.has(item.id) && item.entityKind === 'element')
  const saved: ViewPlace[] = []
  const open: string[] = []
  for (const item of accepted) {
    const place = places.get(item.id)
    if (!place) continue
    saved.push({ elementId: item.id, x: place.x, y: place.y, ...(place.width && place.height ? { width: place.width, height: place.height } : {}) })
    if (place.width) open.push(item.id)
  }
  // New cards change the grid of the cards without a saved place: those keep where they are.
  if (saved.length > 0) for (const [id, at] of unsaved) saved.push({ elementId: id, ...at })
  // A card of the level that got a new child inside it stays open, with the room the child needs.
  const hosts = [...new Set(accepted.map((item) => item.proposed.parentId).filter((id): id is string => typeof id === 'string' && hostSizes.has(id)))]
  const sizes = new Map(hosts.map((id) => [id, hostSizes.get(id)!]))
  if (saved.length > 0 || sizes.size > 0) saveViewPlaces?.(saved, sizes)
  const canvas = useCanvasStore.getState()
  for (const id of [...open, ...hosts]) if (!canvas.expandedNodeIds.has(id)) canvas.toggleExpand(id)
}
