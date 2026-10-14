import { useId } from 'react'
import { useQuery } from '@tanstack/react-query'
import { branchesApi } from '@/lib/branches-api'

/**
 * The open invitations for the signed-in account's email address. The box says that an
 * invitation exists and offers no Accept: only the link that the owner sent, with its
 * secret, accepts or declines it. Nothing shows when there is no invitation.
 */
export function MyInvitations() {
  const id = useId()
  const invitations = useQuery({ queryKey: ['invitations', 'mine'], queryFn: branchesApi.myInvitations })

  const list = invitations.data ?? []
  if (list.length === 0) return null

  return (
    <section aria-labelledby={`${id}-heading`} className="mb-6 rounded-md border border-primary/40 bg-primary/5 p-4">
      <h2 id={`${id}-heading`} className="text-sm font-medium text-foreground">
        {list.length === 1 ? 'You have an invitation' : `You have ${list.length} invitations`}
      </h2>
      <ul className="mt-3 space-y-3">
        {list.map((invitation) => (
          <li key={invitation.id} className="text-sm">
            <p className="text-foreground">
              <span className="font-medium">{invitation.workspaceName}</span> as {invitation.role}
            </p>
            <p className="text-xs text-muted-foreground">
              {invitation.invitedBy ? `From ${invitation.invitedBy}. ` : ''}Expires {new Date(invitation.expiresAt).toLocaleDateString()}.
            </p>
          </li>
        ))}
      </ul>
      <p className="mt-3 text-xs leading-relaxed text-muted-foreground">
        To accept or decline, open the link that the owner sent you. If you have no link, ask the owner for one.
      </p>
    </section>
  )
}
