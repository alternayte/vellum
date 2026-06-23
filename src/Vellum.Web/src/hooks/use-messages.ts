import { useQuery } from '@tanstack/react-query'
import { getApiProjectsByProjectIdMessages } from '@/api/generated'
import '@/api/client'

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
      const result = await getApiProjectsByProjectIdMessages({
        path: { projectId },
        query: { limit: 200, branchId },
      })
      return (result.data as Page<Message>).items
    },
    staleTime: 30_000,
  })
}
