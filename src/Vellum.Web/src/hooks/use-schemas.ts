import { useQuery } from '@tanstack/react-query'
import { getApiProjectsByProjectIdSchemas } from '@/api/generated'
import '@/api/client'

export interface Schema {
  id: string
  name: string
  description: string | null
  content: string
  version: number
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

export function useSchemas(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'schemas'],
    queryFn: async () => {
      const result = await getApiProjectsByProjectIdSchemas({
        path: { projectId },
        query: { limit: 200 },
      })
      return (result.data as Page<Schema>).items
    },
    staleTime: 30_000,
  })
}
