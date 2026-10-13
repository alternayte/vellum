import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteApiProjectsByProjectIdSchemasBySchemaId,
  getApiProjectsByProjectIdSchemas,
  patchApiProjectsByProjectIdSchemasBySchemaId,
  postApiProjectsByProjectIdSchemas,
} from '@/api/generated'
import '@/api/client'
import { shownInline } from '@/lib/mutation-errors'
import { unwrap } from './use-contracts'

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
      // The list is paged; a schema that is missing from a choice reads as "no schema".
      const items: Schema[] = []
      let cursor: string | undefined
      do {
        const page = unwrap<Page<Schema>>(
          await getApiProjectsByProjectIdSchemas({ path: { projectId }, query: { limit: 200, cursor } }),
          'Schemas did not load.',
        )
        items.push(...page.items)
        cursor = page.cursor ?? undefined
      } while (cursor)
      return items
    },
    staleTime: 30_000,
  })
}

/** What a person sets on a schema. The server counts the version: it goes up when the content changes. */
export type SchemaFields = Pick<Schema, 'name' | 'description' | 'content'>

/** A schema belongs to the project, not to a draft: every branch reads the same schemas. */
export function useSchemaMutations(projectId: string) {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'schemas'] })

  return {
    addSchema: useMutation({
      mutationFn: async (schema: SchemaFields) =>
        unwrap<Schema>(
          await postApiProjectsByProjectIdSchemas({ path: { projectId }, body: { id: crypto.randomUUID(), ...schema } }),
          'The schema was not saved.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
    updateSchema: useMutation({
      mutationFn: async ({ id, ...schema }: SchemaFields & { id: string }) =>
        unwrap<Schema>(
          await patchApiProjectsByProjectIdSchemasBySchemaId({
            path: { projectId, schemaId: id },
            body: { ...schema, setDescription: true, setContent: true },
          }),
          'The schema was not saved.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
    removeSchema: useMutation({
      mutationFn: async (schemaId: string) =>
        unwrap<void>(
          await deleteApiProjectsByProjectIdSchemasBySchemaId({ path: { projectId, schemaId } }),
          'The schema was not removed.',
        ),
      onSuccess: refresh,
      onError: shownInline,
    }),
  }
}
