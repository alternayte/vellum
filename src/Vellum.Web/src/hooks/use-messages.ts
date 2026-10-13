import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteApiProjectsByProjectIdMessagesByMessageId,
  getApiProjectsByProjectIdMessages,
  patchApiProjectsByProjectIdMessagesByMessageId,
  postApiProjectsByProjectIdMessages,
} from '@/api/generated'
import '@/api/client'
import { shownInline } from '@/lib/mutation-errors'
import { unwrap } from './use-contracts'

export interface Message {
  id: string
  name: string
  description: string | null
  producerId: string
  consumerIds: string[]
  schemaId: string | null
  tags: string[]
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useMessages(projectId: string, branchId?: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'messages', { branchId }],
    queryFn: async () => {
      // The list is paged; the canvas, the contracts and the scenario editor need every message.
      const items: Message[] = []
      let cursor: string | undefined
      do {
        const page = unwrap<Page<Message>>(
          await getApiProjectsByProjectIdMessages({ path: { projectId }, query: { limit: 200, branchId, cursor } }),
          'Messages did not load.',
        )
        items.push(...page.items)
        cursor = page.cursor ?? undefined
      } while (cursor)
      return items
    },
    staleTime: 30_000,
  })
}

/** Every value of a message that the model holds and a person sets. */
export type MessageFields = Omit<Message, 'id'>

export function useMessageMutations(projectId: string, branchId?: string | null) {
  const queryClient = useQueryClient()
  const query = { branchId: branchId ?? undefined }
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'messages'] })

  return {
    addMessage: useMutation({
      mutationFn: async (message: MessageFields) =>
        unwrap<Message>(
          await postApiProjectsByProjectIdMessages({ path: { projectId }, query, body: { id: crypto.randomUUID(), ...message } }),
          'The message was not saved.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
    updateMessage: useMutation({
      mutationFn: async ({ id, ...message }: Message) =>
        unwrap<Message>(
          await patchApiProjectsByProjectIdMessagesByMessageId({
            path: { projectId, messageId: id },
            query,
            body: { ...message, setDescription: true, setProducerId: true, setConsumerIds: true, setSchemaId: true },
          }),
          'The message was not saved.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
    removeMessage: useMutation({
      mutationFn: async (messageId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdMessagesByMessageId({ path: { projectId, messageId }, query }),
          'The message was not removed.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
  }
}
