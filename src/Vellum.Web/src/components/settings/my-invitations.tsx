import { useId } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { branchesApi, type MyInvitation } from '@/lib/branches-api'
import { shownInline } from '@/lib/mutation-errors'

const describeError = (error: unknown) => (error instanceof Error ? error.message : 'The request did not complete.')

/**
 * The open invitations for the signed-in person, with Accept and Decline. An accept makes
 * the membership with the invited role. Nothing shows when there is no invitation.
 */
export function MyInvitations({ onAccepted }: { onAccepted?: (workspaceId: string) => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const key = ['invitations', 'mine']
  const invitations = useQuery({ queryKey: key, queryFn: branchesApi.myInvitations })

  const settle = () => {
    queryClient.invalidateQueries({ queryKey: key })
    queryClient.invalidateQueries({ queryKey: ['workspaces'] })
  }
  const accept = useMutation({
    mutationFn: (invitation: MyInvitation) => branchesApi.acceptInvitation(invitation.id),
    onSuccess: (_data, invitation) => { settle(); onAccepted?.(invitation.workspaceId) },
    // A closed invitation (revoked or expired meanwhile) stays with its reason until the next read.
    onError: shownInline,
  })
  const decline = useMutation({
    mutationFn: (invitation: MyInvitation) => branchesApi.declineInvitation(invitation.id),
    onSuccess: settle,
    onError: shownInline,
  })

  const list = invitations.data ?? []
  if (list.length === 0) return null
  const busy = accept.isPending || decline.isPending

  return (
    <section aria-labelledby={`${id}-heading`} className="mb-6 rounded-md border border-primary/40 bg-primary/5 p-4">
      <h2 id={`${id}-heading`} className="text-sm font-medium text-foreground">
        {list.length === 1 ? 'You have an invitation' : `You have ${list.length} invitations`}
      </h2>
      <ul className="mt-3 space-y-3">
        {list.map((invitation) => {
          const failed = (accept.isError && accept.variables?.id === invitation.id && accept.error)
            || (decline.isError && decline.variables?.id === invitation.id && decline.error)
          return (
            <li key={invitation.id} className="text-sm">
              <p className="text-foreground">
                <span className="font-medium">{invitation.workspaceName}</span> as {invitation.role}
              </p>
              <p className="text-xs text-muted-foreground">
                {invitation.invitedBy ? `From ${invitation.invitedBy}. ` : ''}Expires {new Date(invitation.expiresAt).toLocaleDateString()}.
              </p>
              <div className="mt-2 flex gap-2">
                <Button size="sm" disabled={busy} onClick={() => accept.mutate(invitation)} aria-label={`Accept the invitation to ${invitation.workspaceName}`}>
                  Accept
                </Button>
                <Button size="sm" variant="ghost" disabled={busy} onClick={() => decline.mutate(invitation)} aria-label={`Decline the invitation to ${invitation.workspaceName}`}>
                  Decline
                </Button>
              </div>
              {failed && <p role="alert" className="mt-1 text-xs text-destructive">{describeError(failed)}</p>}
            </li>
          )
        })}
      </ul>
    </section>
  )
}
