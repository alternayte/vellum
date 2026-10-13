import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteApiProjectsByProjectIdChannelsByChannelId,
  deleteApiProjectsByProjectIdOperationsByOperationId,
  deleteApiProjectsByProjectIdSubscriptionsBySubscriptionId,
  getApiProjectsByProjectIdChannels,
  getApiProjectsByProjectIdOperations,
  getApiProjectsByProjectIdSubscriptions,
  postApiProjectsByProjectIdChannels,
  postApiProjectsByProjectIdOperations,
  postApiProjectsByProjectIdSubscriptions,
} from '@/api/generated'
import '@/api/client'
import { shownInline } from '@/lib/mutation-errors'

export interface RetryPolicy {
  maxAttempts: number
  backoffMs: number
}

export interface Operation {
  id: string
  ownerId: string
  name: string
  transport: string
  requestSchemaId: string | null
  responseSchemaId: string | null
  timeoutMs: number
  retry: RetryPolicy
  factStatus: string
}

export interface Channel {
  id: string
  name: string
  kind: string
  factStatus: string
}

export interface Subscription {
  id: string
  channelId: string
  consumerId: string
  retry: RetryPolicy
  deadLetterChannelId: string | null
  factStatus: string
}

interface Page<T> {
  items: T[]
}

/** The server's reason for a refused write (the shared error envelope), so the UI can say why. */
export function apiErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const e = error as { title?: unknown; detail?: unknown; errors?: { message?: string }[] | null }
    if (Array.isArray(e.errors) && e.errors[0]?.message) return e.errors[0].message
    if (typeof e.title === 'string' && e.title) return typeof e.detail === 'string' && e.detail ? `${e.title} ${e.detail}` : e.title
  }
  return fallback
}

function unwrap<T>(result: { data?: unknown; error?: unknown }, fallback: string): T {
  if (result.error !== undefined) throw new Error(apiErrorMessage(result.error, fallback))
  return result.data as T
}

const key = (projectId: string, kind: string, branchId?: string | null) =>
  ['projects', projectId, kind, { branchId: branchId ?? undefined }] as const

export function useOperations(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'operations', branchId),
    queryFn: async () =>
      unwrap<Page<Operation>>(
        await getApiProjectsByProjectIdOperations({ path: { projectId }, query: { branchId: branchId ?? undefined } }),
        'Operations did not load.',
      ).items,
    staleTime: 30_000,
  })
}

export function useChannels(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'channels', branchId),
    queryFn: async () =>
      unwrap<Page<Channel>>(
        await getApiProjectsByProjectIdChannels({ path: { projectId }, query: { branchId: branchId ?? undefined } }),
        'Channels did not load.',
      ).items,
    staleTime: 30_000,
  })
}

export function useSubscriptions(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'subscriptions', branchId),
    queryFn: async () =>
      unwrap<Page<Subscription>>(
        await getApiProjectsByProjectIdSubscriptions({ path: { projectId }, query: { branchId: branchId ?? undefined } }),
        'Subscriptions did not load.',
      ).items,
    staleTime: 30_000,
  })
}

export type NewOperation = Pick<Operation, 'ownerId' | 'name' | 'transport' | 'timeoutMs' | 'retry'> & {
  requestSchemaId?: string | null
  responseSchemaId?: string | null
}
export type NewChannel = Pick<Channel, 'name' | 'kind'>
export type NewSubscription = Pick<Subscription, 'channelId' | 'consumerId' | 'retry' | 'deadLetterChannelId'>

export function useContractMutations(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const query = { branchId: branchId ?? undefined }
  const refresh = (kind: string) => () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, kind] })

  return {
    addOperation: useMutation({
      mutationFn: async (operation: NewOperation) =>
        unwrap<Operation>(
          await postApiProjectsByProjectIdOperations({
            path: { projectId },
            query,
            body: {
              id: crypto.randomUUID(),
              requestSchemaId: null,
              responseSchemaId: null,
              ...operation,
            },
          }),
          'The operation was not saved.',
        ),
      onSuccess: refresh('operations'),
      onError: shownInline,
    }),
    removeOperation: useMutation({
      mutationFn: async (operationId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdOperationsByOperationId({ path: { projectId, operationId }, query }),
          'The operation was not removed.',
        ),
      onSuccess: refresh('operations'),
      onError: shownInline,
    }),
    addChannel: useMutation({
      mutationFn: async (channel: NewChannel) =>
        unwrap<Channel>(
          await postApiProjectsByProjectIdChannels({ path: { projectId }, query, body: { id: crypto.randomUUID(), ...channel } }),
          'The channel was not saved.',
        ),
      onSuccess: refresh('channels'),
      onError: shownInline,
    }),
    removeChannel: useMutation({
      mutationFn: async (channelId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdChannelsByChannelId({ path: { projectId, channelId }, query }),
          'The channel was not removed.',
        ),
      onSuccess: refresh('channels'),
      onError: shownInline,
    }),
    addSubscription: useMutation({
      mutationFn: async (subscription: NewSubscription) =>
        unwrap<Subscription>(
          await postApiProjectsByProjectIdSubscriptions({
            path: { projectId },
            query,
            body: { id: crypto.randomUUID(), ...subscription },
          }),
          'The subscription was not saved.',
        ),
      onSuccess: refresh('subscriptions'),
      onError: shownInline,
    }),
    removeSubscription: useMutation({
      mutationFn: async (subscriptionId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdSubscriptionsBySubscriptionId({ path: { projectId, subscriptionId }, query }),
          'The subscription was not removed.',
        ),
      onSuccess: refresh('subscriptions'),
      onError: shownInline,
    }),
  }
}
