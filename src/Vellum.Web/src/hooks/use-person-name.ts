import { useQuery } from '@tanstack/react-query'
import { branchesApi, personName } from '@/lib/branches-api'
import { useProject } from '@/hooks/use-model'

/** The name of a member of the workspace of a project, by user ID, for "who" in a record. */
export function usePersonName(projectId: string): (userId: string) => string {
  const workspaceId = useProject(projectId).data?.workspaceId
  const members = useQuery({
    queryKey: ['workspaces', workspaceId, 'members'],
    queryFn: () => branchesApi.members(workspaceId!),
    enabled: Boolean(workspaceId),
  })
  return (userId) => personName(members.data?.members, userId)
}
