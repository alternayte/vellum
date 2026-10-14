import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '@/lib/settings-api'
import type { EntityType } from '@/lib/branches-api'
import { shownInline } from '@/lib/mutation-errors'
import type { Provenance } from '@/lib/fact-status'

/** The label of a fact after a person changed it. */
export interface FactLabel {
  entityType: EntityType
  entityId: string
  factStatus: string
  provenance: Provenance | null
}

/**
 * The one label change a person can make to a fact, or null when there is none. A person never
 * types a label: `measured` and `observed-in-source` claim evidence, and only an import or a
 * measured result gives that.
 */
export function labelAction(factStatus: string | null | undefined): 'accept' | 'mark-unknown' | null {
  const status = factStatus ?? 'declared'
  if (status === 'inferred' || status === 'observed-in-source' || status === 'unknown') return 'accept'
  if (status === 'declared') return 'mark-unknown'
  return null
}

export const LABEL_ACTION_TEXT = { accept: 'Accept as declared', 'mark-unknown': 'Mark as unknown' } as const

/** The model lists that hold each kind of fact, so a change shows on the canvas and in the panels. */
const LIST_OF: Record<EntityType, string> = {
  element: 'elements',
  relationship: 'relationships',
  message: 'messages',
  operation: 'operations',
  channel: 'channels',
  subscription: 'subscriptions',
  binding: 'bindings',
}

/** Accepts a fact as declared or marks it as unknown, on the branch that the workbench shows. The server stamps who and when. */
export function useFactLabel(projectId: string, branchId: string | null | undefined) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ entityType, entityId, action }: { entityType: EntityType; entityId: string; action: 'accept' | 'mark-unknown' }) =>
      api<FactLabel>('POST', `/api/projects/${projectId}/facts/${entityType}/${entityId}/${action}${branchId ? `?branchId=${branchId}` : ''}`),
    onSuccess: (label) => queryClient.invalidateQueries({ queryKey: ['projects', projectId, LIST_OF[label.entityType]] }),
    onError: shownInline,
  })
}
