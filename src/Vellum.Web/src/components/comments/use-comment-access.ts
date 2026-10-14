import { useQuery } from '@tanstack/react-query'
import { useAuth } from '@/hooks/use-auth'
import { useProject } from '@/hooks/use-model'
import { branchesApi, canComment, canEditModel, personName, type Member } from '@/lib/branches-api'
import { useDraftStore } from '@/stores/draft-store'

export interface CommentAccess {
  /** The draft whose threads show: the selected draft, or null for main. */
  draftId: string | null
  userId: string | undefined
  members: Member[]
  /** The person writes comments here: a Commenter or above, on main or on an open draft. */
  canWrite: boolean
  /** An Editor or an Owner: resolves any thread and raises a thread as a decision. */
  isEditor: boolean
  isOwner: boolean
  nameOf: (userId: string) => string
}

/**
 * Who may do what with the comments of a project. Threads belong to main or to a draft;
 * a named revision and a variant show the threads of main and take none.
 */
export function useCommentAccess(projectId: string): CommentAccess {
  const { data: project } = useProject(projectId)
  const { user } = useAuth()
  const selection = useDraftStore((s) => s.selection)
  const activeDraftId = useDraftStore((s) => s.activeDraftId)
  const workspaceId = project?.workspaceId
  const members = useQuery({
    queryKey: ['workspaces', workspaceId, 'members'],
    queryFn: () => branchesApi.members(workspaceId!),
    enabled: Boolean(workspaceId),
  })
  const role = project?.yourRole
  return {
    draftId: activeDraftId,
    userId: user?.id,
    members: members.data?.members ?? [],
    canWrite: canComment(role) && (selection.kind === 'main' || selection.kind === 'draft'),
    isEditor: canEditModel(role),
    isOwner: role === 'Owner',
    nameOf: (id) => personName(members.data?.members, id),
  }
}
