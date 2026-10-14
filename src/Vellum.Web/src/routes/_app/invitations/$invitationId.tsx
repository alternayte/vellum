import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/hooks/use-auth'
import { branchesApi } from '@/lib/branches-api'
import { clearPendingInvitation, forgetInvitation, invitationSecret } from '@/lib/invitation-link'
import { shownInline } from '@/lib/mutation-errors'

export const Route = createFileRoute('/_app/invitations/$invitationId')({
  component: InvitationPage,
})

const describeError = (error: unknown) => (error instanceof Error ? error.message : 'The request did not complete.')

/**
 * The page of an invitation link. The secret comes from the fragment of the link. The server
 * shows the invitation only to the account of the invited address with the right secret,
 * and gives one answer for every other case, so the page cannot say which part is wrong.
 */
function InvitationPage() {
  const { invitationId } = Route.useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [secret] = useState(() => invitationSecret(invitationId))
  const [declined, setDeclined] = useState(false)

  useEffect(() => clearPendingInvitation(), [])

  const invitation = useQuery({
    queryKey: ['invitations', invitationId, 'open'],
    queryFn: () => branchesApi.openInvitation(invitationId, secret!),
    enabled: !!secret,
    retry: false,
    staleTime: Infinity,
  })

  const settle = () => {
    forgetInvitation(invitationId)
    queryClient.invalidateQueries({ queryKey: ['invitations', 'mine'] })
    queryClient.invalidateQueries({ queryKey: ['workspaces'] })
  }
  const accept = useMutation({
    mutationFn: () => branchesApi.acceptInvitation(invitationId, secret!),
    onSuccess: () => { settle(); navigate({ to: '/' }) },
    onError: shownInline,
  })
  const decline = useMutation({
    mutationFn: () => branchesApi.declineInvitation(invitationId, secret!),
    onSuccess: () => { settle(); setDeclined(true) },
    onError: shownInline,
  })

  const data = invitation.data
  const busy = accept.isPending || decline.isPending
  const failed = accept.error ?? decline.error

  return (
    <div className="flex flex-1 items-center justify-center p-4">
      <div className="w-full max-w-md rounded-lg border border-border bg-card p-8">
        <h1 className="mb-4 font-display text-2xl font-bold">Invitation</h1>

        {!secret && (
          <p className="text-sm text-muted-foreground">
            This address has no secret. Open the whole link that the owner sent you, or ask the owner for a new one.
          </p>
        )}

        {secret && invitation.isLoading && <p className="text-sm text-muted-foreground">Reading the invitation…</p>}

        {secret && invitation.isError && (
          <div role="alert" className="space-y-2 text-sm">
            <p className="text-foreground">Vellum found no invitation for this link and this account.</p>
            <p className="text-muted-foreground">
              You are signed in as {user?.email}. The invitation is for one email address: sign in with the account of that address.
              A link stops working when the owner invites the address again. Ask the owner for the newest link.
            </p>
          </div>
        )}

        {data && declined && (
          <p role="status" className="text-sm text-muted-foreground">You declined the invitation to {data.workspaceName}.</p>
        )}

        {data && !declined && (
          <>
            <p className="text-sm text-foreground">
              <span className="font-medium">{data.workspaceName}</span> as {data.role}
            </p>
            <p className="mt-1 text-xs text-muted-foreground">
              {data.invitedBy ? `From ${data.invitedBy}. ` : ''}Expires {new Date(data.expiresAt).toLocaleDateString()}.
            </p>
            {data.status === 'pending' ? (
              <div className="mt-4 flex gap-2">
                <Button disabled={busy} onClick={() => accept.mutate()}>{accept.isPending ? 'Accepting…' : 'Accept'}</Button>
                <Button variant="ghost" disabled={busy} onClick={() => decline.mutate()}>Decline</Button>
              </div>
            ) : (
              <p className="mt-4 text-sm text-muted-foreground">The invitation is {data.status}. Ask the owner to invite you again.</p>
            )}
            {failed && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(failed)}</p>}
          </>
        )}

        <Button variant="ghost" size="sm" className="mt-6 px-0 text-xs" onClick={() => navigate({ to: '/' })}>Go to the workspaces</Button>
      </div>
    </div>
  )
}
