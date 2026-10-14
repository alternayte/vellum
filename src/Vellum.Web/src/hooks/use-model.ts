import { useQuery } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useChannels, useOperations, useSubscriptions } from '@/hooks/use-contracts'
import { useElements } from '@/hooks/use-elements'
import { useMessages } from '@/hooks/use-messages'
import { useRelationships } from '@/hooks/use-relationships'
import { branchesApi } from '@/lib/branches-api'
import { ApiError } from '@/lib/settings-api'
import { useDraftStore } from '@/stores/draft-store'

/** The project with the role of the signed-in person in its workspace. */
export function useProject(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'detail'],
    queryFn: () => branchesApi.project(projectId),
    // A project that does not exist, or that the person may not read, stays so: no retry.
    retry: (failures, error) => !isProjectRefusal(error) && failures < 3,
  })
}

/** The answer for a project ID that names no project the signed-in person can read. */
export function isProjectRefusal(error: unknown): boolean {
  return error instanceof ApiError && [400, 403, 404].includes(error.status)
}

/**
 * Whether a project error replaces the workbench. A refusal always does. Any other error does
 * only when no project is loaded yet: a failed background refetch keeps the loaded workbench.
 */
export function projectErrorBlocks(hasProject: boolean, error: unknown): boolean {
  return !!error && (!hasProject || isProjectRefusal(error))
}

/**
 * The model the workbench shows: the lists of the selected branch, or the stored state of
 * the selected revision. A revision has no lists of its own, so one request gives all of it.
 */
export function useModel(projectId: string) {
  const selection = useDraftStore((s) => s.selection)
  const branchId = useDraftStore((s) => s.activeDraftStreamId) ?? undefined
  const revisionId = selection.kind === 'revision' ? selection.id : null

  const elements = useElements(projectId, branchId)
  const relationships = useRelationships(projectId, branchId)
  const messages = useMessages(projectId, branchId)
  const operations = useOperations(projectId, branchId)
  const channels = useChannels(projectId, branchId)
  const subscriptions = useSubscriptions(projectId, branchId)
  const revision = useQuery({
    queryKey: ['projects', projectId, 'revisions', revisionId, 'model'],
    queryFn: () => branchesApi.revisionModel(projectId, revisionId!),
    enabled: revisionId !== null,
    // A revision never changes.
    staleTime: Infinity,
  })

  if (revisionId !== null) {
    return {
      elements: revision.data?.elements,
      relationships: revision.data?.relationships,
      messages: revision.data?.messages,
      operations: revision.data?.operations,
      channels: revision.data?.channels,
      subscriptions: revision.data?.subscriptions,
      isLoading: revision.isLoading,
      error: revision.error,
    }
  }

  return {
    elements: elements.data,
    relationships: relationships.data,
    messages: messages.data,
    operations: operations.data,
    channels: channels.data,
    subscriptions: subscriptions.data,
    isLoading: elements.isLoading || relationships.isLoading,
    error: elements.error ?? relationships.error,
  }
}

/** Says that a write did not happen because the model is read-only. One message, however many writes one action makes. */
export function refuseWrite() {
  toast.message('This model is read-only. Nothing was changed.', { id: 'read-only-model' })
}

/**
 * Wraps a mutation so that a read-only model takes no write from it. The model is
 * read-only when it is a named revision or when the person is a viewer.
 */
export function writeGuard(readOnly: boolean) {
  return <M extends { mutate: (...args: never[]) => void }>(mutation: M): M =>
    readOnly ? { ...mutation, mutate: refuseWrite as M['mutate'] } : mutation
}
