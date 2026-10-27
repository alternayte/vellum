import { useQuery } from '@tanstack/react-query'
import { getApiWorkspaces } from '@/api/generated'
import '@/api/client'

export interface MyWorkspace {
  id: string
  name: string
}

/** The workspaces of the signed-in person, as the project list reads them. */
export function useMyWorkspaces() {
  return useQuery({
    queryKey: ['workspaces'],
    queryFn: async () => (await getApiWorkspaces()).data as MyWorkspace[],
  })
}

/** Up to two letters of the name, or the first letter of the email when there is no name. */
export function initialsOf(name: string | null | undefined, email: string | null | undefined): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean)
  if (words.length > 0) return words.map((w) => w[0]).join('').toUpperCase().slice(0, 2)
  return (email ?? '?').slice(0, 1).toUpperCase()
}
