import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteApiProjectsByProjectIdBindingsByBindingId,
  deleteApiProjectsByProjectIdChannelsByChannelId,
  deleteApiProjectsByProjectIdOperationsByOperationId,
  deleteApiProjectsByProjectIdSubscriptionsBySubscriptionId,
  getApiProjectsByProjectIdBindings,
  getApiProjectsByProjectIdChannels,
  getApiProjectsByProjectIdOperations,
  getApiProjectsByProjectIdSubscriptions,
  patchApiProjectsByProjectIdBindingsByBindingId,
  patchApiProjectsByProjectIdChannelsByChannelId,
  patchApiProjectsByProjectIdOperationsByOperationId,
  patchApiProjectsByProjectIdSubscriptionsBySubscriptionId,
  postApiProjectsByProjectIdBindings,
  postApiProjectsByProjectIdChannels,
  postApiProjectsByProjectIdOperations,
  postApiProjectsByProjectIdSubscriptions,
} from '@/api/generated'
import '@/api/client'
import { shownInline } from '@/lib/mutation-errors'
import type { Provenance } from '@/lib/fact-status'

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
  provenance?: Provenance | null
}

export interface Channel {
  id: string
  name: string
  kind: string
  factStatus: string
  provenance?: Provenance | null
}

export interface Subscription {
  id: string
  channelId: string
  consumerId: string
  retry: RetryPolicy
  deadLetterChannelId: string | null
  factStatus: string
  provenance?: Provenance | null
}

/** A route from an exchange or a topic to a queue or an exchange. A null key passes every message. */
export interface Binding {
  id: string
  sourceChannelId: string
  targetChannelId: string
  routingKey: string | null
  factStatus: string
  provenance?: Provenance | null
}

/** The channel kinds a binding may start at and end at. The server holds the same rule. */
export const canRouteFrom = (kind: string) => kind === 'exchange' || kind === 'topic'
export const canRouteTo = (kind: string) => kind === 'queue' || kind === 'exchange'

/**
 * The element kinds that act as a service: they offer operations and consume subscriptions.
 * An actor is a person or an outside party and a store holds data; the server's build slices
 * leave both out of the services in the same way.
 */
export const isServiceKind = (kind: string | undefined) => kind !== 'actor' && kind !== 'store'

interface Page<T> {
  items: T[]
  cursor: string | null
}

type PageQuery = { branchId?: string; limit: number; cursor?: string }

/** Contract lists are paged; the canvas and the scenario editor need every item. */
async function allPages<T>(
  fetchPage: (query: PageQuery) => Promise<{ data?: unknown; error?: unknown }>,
  branchId: string | null | undefined,
  fallback: string,
): Promise<T[]> {
  const items: T[] = []
  let cursor: string | undefined
  do {
    const page = unwrap<Page<T>>(await fetchPage({ branchId: branchId ?? undefined, limit: 200, cursor }), fallback)
    items.push(...page.items)
    cursor = page.cursor ?? undefined
  } while (cursor)
  return items
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

/** A refused write. `fields` holds the server's reason for each request field it names. */
export class ApiError extends Error {
  readonly fields: Record<string, string>

  constructor(message: string, fields: Record<string, string> = {}) {
    super(message)
    this.name = 'ApiError'
    this.fields = fields
  }
}

/** The reason for each field that the error envelope names; the first reason of a field wins. */
export function apiFieldErrors(error: unknown): Record<string, string> {
  const fields: Record<string, string> = {}
  const errors = error && typeof error === 'object' ? (error as { errors?: unknown }).errors : null
  if (!Array.isArray(errors)) return fields
  for (const entry of errors as { field?: unknown; message?: unknown }[]) {
    if (typeof entry?.field === 'string' && typeof entry.message === 'string' && !(entry.field in fields)) fields[entry.field] = entry.message
  }
  return fields
}

/** What a refused write says: a reason for each field the form shows, or one reason for the form. */
export interface FormFailure {
  fields: Record<string, string>
  message: string | null
}

export const NO_FAILURE: FormFailure = { fields: {}, message: null }

/**
 * Sorts the server's reasons by the fields a form shows. A reason for a field that the form
 * does not show becomes the reason of the form, so no refusal is lost.
 */
export function formFailure(error: Error, shown: string[]): FormFailure {
  const all = error instanceof ApiError ? error.fields : {}
  const fields = Object.fromEntries(Object.entries(all).filter(([field]) => shown.includes(field)))
  return { fields, message: Object.keys(fields).length > 0 ? null : error.message }
}

export function unwrap<T>(result: { data?: unknown; error?: unknown }, fallback: string): T {
  if (result.error !== undefined) throw new ApiError(apiErrorMessage(result.error, fallback), apiFieldErrors(result.error))
  return result.data as T
}

const key = (projectId: string, kind: string, branchId?: string | null) =>
  ['projects', projectId, kind, { branchId: branchId ?? undefined }] as const

export function useOperations(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'operations', branchId),
    queryFn: () => allPages<Operation>((query) => getApiProjectsByProjectIdOperations({ path: { projectId }, query }), branchId, 'Operations did not load.'),
    staleTime: 30_000,
  })
}

export function useChannels(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'channels', branchId),
    queryFn: () => allPages<Channel>((query) => getApiProjectsByProjectIdChannels({ path: { projectId }, query }), branchId, 'Channels did not load.'),
    staleTime: 30_000,
  })
}

export function useSubscriptions(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'subscriptions', branchId),
    queryFn: () => allPages<Subscription>((query) => getApiProjectsByProjectIdSubscriptions({ path: { projectId }, query }), branchId, 'Subscriptions did not load.'),
    staleTime: 30_000,
  })
}

export function useBindings(projectId: string, branchId?: string | null) {
  return useQuery({
    queryKey: key(projectId, 'bindings', branchId),
    queryFn: () => allPages<Binding>((query) => getApiProjectsByProjectIdBindings({ path: { projectId }, query }), branchId, 'Bindings did not load.'),
    staleTime: 30_000,
  })
}

export type NewOperation = Pick<Operation, 'ownerId' | 'name' | 'transport' | 'timeoutMs' | 'retry'> & {
  requestSchemaId?: string | null
  responseSchemaId?: string | null
}
/** A null schema ID clears the schema of the operation. */
export type OperationChange = NewOperation & { id: string; requestSchemaId: string | null; responseSchemaId: string | null }
export type NewChannel = Pick<Channel, 'name' | 'kind'>
export type NewSubscription = Pick<Subscription, 'channelId' | 'consumerId' | 'retry' | 'deadLetterChannelId'>
export type NewBinding = Pick<Binding, 'sourceChannelId' | 'targetChannelId' | 'routingKey'>

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
    updateOperation: useMutation({
      mutationFn: async ({ id, ...operation }: OperationChange) =>
        unwrap<Operation>(
          await patchApiProjectsByProjectIdOperationsByOperationId({
            path: { projectId, operationId: id },
            query,
            body: { ...operation, setRequestSchemaId: true, setResponseSchemaId: true },
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
    updateChannel: useMutation({
      mutationFn: async ({ id, ...channel }: NewChannel & { id: string }) =>
        unwrap<Channel>(
          await patchApiProjectsByProjectIdChannelsByChannelId({ path: { projectId, channelId: id }, query, body: channel }),
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
    updateSubscription: useMutation({
      // A null dead-letter channel removes it: the request says so with setDeadLetterChannelId.
      mutationFn: async ({ id, ...subscription }: NewSubscription & { id: string }) =>
        unwrap<Subscription>(
          await patchApiProjectsByProjectIdSubscriptionsBySubscriptionId({
            path: { projectId, subscriptionId: id },
            query,
            body: { ...subscription, setDeadLetterChannelId: true },
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
    addBinding: useMutation({
      mutationFn: async (binding: NewBinding) =>
        unwrap<Binding>(
          await postApiProjectsByProjectIdBindings({ path: { projectId }, query, body: { id: crypto.randomUUID(), ...binding } }),
          'The binding was not saved.',
        ),
      onSuccess: refresh('bindings'),
      onError: shownInline,
    }),
    updateBinding: useMutation({
      // A null routing key passes every message: the request says so with setRoutingKey.
      mutationFn: async ({ id, targetChannelId, routingKey }: Pick<Binding, 'id' | 'targetChannelId' | 'routingKey'>) =>
        unwrap<Binding>(
          await patchApiProjectsByProjectIdBindingsByBindingId({
            path: { projectId, bindingId: id },
            query,
            body: { sourceChannelId: null, targetChannelId, routingKey, setRoutingKey: true },
          }),
          'The binding was not saved.',
        ),
      onSuccess: refresh('bindings'),
      onError: shownInline,
    }),
    removeBinding: useMutation({
      mutationFn: async (bindingId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdBindingsByBindingId({ path: { projectId, bindingId }, query }),
          'The binding was not removed.',
        ),
      onSuccess: refresh('bindings'),
      onError: shownInline,
    }),
  }
}
