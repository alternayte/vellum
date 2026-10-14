import { useId, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { SectionHeading, selectClass } from '@/components/settings/fields'
import { useAuth } from '@/hooks/use-auth'
import { branchesApi, type Invitation, type Member, type Role } from '@/lib/branches-api'
import { ApiError } from '@/lib/settings-api'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'

const ROLES: { id: Role; can: string }[] = [
  { id: 'Viewer', can: 'Reads the projects, the models, the documents, the review and the estimates.' },
  { id: 'Editor', can: 'Does what a viewer does. Changes the models, the scenarios, the documents, the drafts, the variants and the cost sheets.' },
  { id: 'Owner', can: 'Does what an editor does. Invites and removes members, changes the settings and the secrets, approves promises and accepts decisions.' },
]

/** The reason the server gave: the message of the refused field when it names one, else its title and detail. */
const describeError = (error: unknown) =>
  error instanceof ApiError && error.fields.length > 0 ? error.fields[0].message : error instanceof Error ? error.message : 'The request did not complete.'

const nameOf = (member: Member) => member.displayName || member.email || `user ${member.userId.slice(0, 8)}`

const dateOf = (iso: string) => new Date(iso).toLocaleDateString()

/**
 * The members of the workspace and their roles. Every member reads the list. An owner
 * invites a person by email address, sees the open invitations and revokes one, changes a
 * role and removes a member. The person accepts the invitation after sign-in, on the
 * workspace list. The server keeps one owner at least; its refusal is shown on the row.
 */
export function Members({ workspaceId, registrationOpen }: { workspaceId: string; registrationOpen: boolean | undefined }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const { user } = useAuth()
  const id = useId()
  const key = ['workspaces', workspaceId, 'members']
  const members = useQuery({ queryKey: key, queryFn: () => branchesApi.members(workspaceId) })
  const isOwner = members.data?.yourRole === 'Owner'
  const invitationsKey = ['workspaces', workspaceId, 'invitations']
  const invitations = useQuery({ queryKey: invitationsKey, queryFn: () => branchesApi.invitations(workspaceId), enabled: isOwner })
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<Role>('Editor')
  const [removing, setRemoving] = useState<Member | null>(null)
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: key })
    queryClient.invalidateQueries({ queryKey: invitationsKey })
    queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'audit'] })
  }

  const [invited, setInvited] = useState<Invitation | null>(null)
  const invite = useMutation({
    mutationFn: () => branchesApi.invite(workspaceId, email.trim(), role),
    onSuccess: (invitation) => { setEmail(''); setInvited(invitation); refresh() },
    onError: shownInline,
  })
  const revoke = useMutation({
    mutationFn: (invitation: Invitation) => branchesApi.revokeInvitation(workspaceId, invitation.id),
    onSuccess: refresh,
    onError: shownInline,
  })
  const change = useMutation({
    mutationFn: (input: { userId: string; role: Role }) => branchesApi.changeRole(workspaceId, input.userId, input.role),
    onSuccess: refresh,
    onError: shownInline,
  })
  const remove = useMutation({
    mutationFn: (member: Member) => branchesApi.removeMember(workspaceId, member.userId),
    onSuccess: (_data, member) => {
      setRemoving(null)
      // A person who removed their own membership has no access to this page any more.
      if (member.userId === user?.id) navigate({ to: '/' })
      else refresh()
    },
    onError: shownInline,
  })

  const list = members.data?.members ?? []
  const open = invitations.data ?? []

  return (
    <>
      <SectionHeading title="Members" description="The people who can open this workspace. A member has one role, and the role applies to each project of the workspace." />

      {members.isLoading && <p className="mt-4 text-sm text-muted-foreground">Reading the members…</p>}
      {members.isError && <p role="alert" className="mt-4 text-sm text-destructive">{describeError(members.error)}</p>}

      {members.data && (
        <div className="mt-4 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">Person</th>
                <th className="px-3 py-2 font-medium">Role</th>
                <th className="px-3 py-2 font-medium max-sm:hidden">Member since</th>
                {isOwner && <th className="px-3 py-2 font-medium"><span className="sr-only">Remove</span></th>}
              </tr>
            </thead>
            <tbody>
              {list.map((member) => {
                const failed = change.isError && change.variables?.userId === member.userId
                return (
                  <tr key={member.userId} className="border-b border-border align-top last:border-b-0">
                    <td className="min-w-28 px-3 py-2">
                      <p className="text-foreground">{nameOf(member)}{member.userId === user?.id && <span className="text-muted-foreground"> (you)</span>}</p>
                      {member.displayName && member.email && <p className="text-xs text-muted-foreground [overflow-wrap:anywhere]">{member.email}</p>}
                      {!member.email && <p className="text-xs text-muted-foreground">The account does not exist any more.</p>}
                    </td>
                    <td className="px-3 py-2">
                      {isOwner ? (
                        <select
                          className={cn(selectClass, 'w-28')}
                          aria-label={`Role of ${nameOf(member)}`}
                          aria-invalid={failed ? true : undefined}
                          aria-describedby={failed ? `${id}-${member.userId}-error` : undefined}
                          value={member.role}
                          disabled={change.isPending}
                          onChange={(e) => change.mutate({ userId: member.userId, role: e.target.value as Role })}
                        >
                          {ROLES.map((r) => <option key={r.id} value={r.id}>{r.id}</option>)}
                        </select>
                      ) : member.role}
                      {failed && <p id={`${id}-${member.userId}-error`} role="alert" className="mt-1 max-w-64 text-xs text-destructive">{describeError(change.error)}</p>}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-muted-foreground max-sm:hidden">{member.createdAt ? new Date(member.createdAt).toLocaleDateString() : 'Not recorded'}</td>
                    {isOwner && (
                      <td className="px-1 py-2 text-right sm:px-3">
                        <Button variant="ghost" size="sm" aria-label={`Remove ${nameOf(member)}`} onClick={() => { remove.reset(); setRemoving(member) }}>Remove</Button>
                      </td>
                    )}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {members.data && !isOwner && (
        <p className="mt-3 text-xs text-muted-foreground">Your role is {members.data.yourRole}. Only an owner invites people, changes a role and removes a member.</p>
      )}

      {isOwner && (
        <section className="mt-6" aria-labelledby={`${id}-open-heading`}>
          <h3 id={`${id}-open-heading`} className="text-sm font-medium text-foreground">Open invitations</h3>
          {invitations.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(invitations.error)}</p>}
          {invitations.data && open.length === 0 && <p className="mt-1 text-xs text-muted-foreground">No invitation is open.</p>}
          {open.length > 0 && (
            <div className="mt-2 overflow-x-auto rounded-lg border border-border">
              <table className="w-full text-left text-sm">
                <thead className="border-b border-border text-xs text-muted-foreground">
                  <tr>
                    <th className="px-3 py-2 font-medium">Email address</th>
                    <th className="px-3 py-2 font-medium">Role</th>
                    <th className="px-3 py-2 font-medium max-sm:hidden">Expires</th>
                    <th className="px-3 py-2 font-medium"><span className="sr-only">Revoke</span></th>
                  </tr>
                </thead>
                <tbody>
                  {open.map((invitation) => {
                    const failed = revoke.isError && revoke.variables?.id === invitation.id
                    return (
                      <tr key={invitation.id} className="border-b border-border align-top last:border-b-0">
                        <td className="min-w-28 px-3 py-2 text-foreground [overflow-wrap:anywhere]">
                          {invitation.email}
                          {failed && <p role="alert" className="mt-1 text-xs text-destructive">The invitation was not revoked. {describeError(revoke.error)}</p>}
                        </td>
                        <td className="px-3 py-2">{invitation.role}</td>
                        <td className="whitespace-nowrap px-3 py-2 text-muted-foreground max-sm:hidden">{dateOf(invitation.expiresAt)}</td>
                        <td className="px-1 py-2 text-right sm:px-3">
                          <Button
                            variant="ghost" size="sm" aria-label={`Revoke the invitation of ${invitation.email}`}
                            disabled={revoke.isPending && revoke.variables?.id === invitation.id}
                            onClick={() => revoke.mutate(invitation)}
                          >
                            Revoke
                          </Button>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {isOwner && (
        <form className="mt-6" onSubmit={(e) => { e.preventDefault(); if (email.trim()) { setInvited(null); invite.mutate() } }}>
          <h3 className="text-sm font-medium text-foreground">Invite a person</h3>
          <p id={`${id}-add-hint`} className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
            The person signs in with an account that has this email address, then accepts the invitation on the workspace list.
            Vellum sends no email: tell the person yourself. An invitation expires after 14 days.
            {registrationOpen === false && ' Registration is closed, so a person with no account cannot create one now. The instance administrator opens it in Instance.'}
          </p>
          <div className="mt-3 flex flex-wrap items-end gap-2">
            <div className="min-w-0 flex-1 basis-56">
              <label htmlFor={`${id}-email`} className="mb-1 block text-xs font-medium text-muted-foreground">Email address</label>
              <Input
                id={`${id}-email`} type="email" value={email} required maxLength={256}
                aria-invalid={invite.isError ? true : undefined}
                aria-describedby={invite.isError ? `${id}-add-hint ${id}-add-error` : `${id}-add-hint`}
                onChange={(e) => setEmail(e.target.value)}
              />
            </div>
            <div>
              <label htmlFor={`${id}-role`} className="mb-1 block text-xs font-medium text-muted-foreground">Role</label>
              <select id={`${id}-role`} className={cn(selectClass, 'w-28')} value={role} onChange={(e) => setRole(e.target.value as Role)}>
                {ROLES.map((r) => <option key={r.id} value={r.id}>{r.id}</option>)}
              </select>
            </div>
            <Button type="submit" disabled={!email.trim() || invite.isPending}>{invite.isPending ? 'Inviting…' : 'Invite'}</Button>
          </div>
          {invite.isError && <p id={`${id}-add-error`} role="alert" className="mt-2 text-xs text-destructive">No invitation was made. {describeError(invite.error)}</p>}
          {invited && !invite.isError && (
            <p role="status" className="mt-2 text-xs text-muted-foreground">
              {invited.email} is invited as {invited.role} until {dateOf(invited.expiresAt)}.
            </p>
          )}
        </form>
      )}

      <h3 className="mt-8 text-sm font-medium text-foreground">What a role can do</h3>
      <dl className="mt-2 max-w-prose space-y-1.5 text-xs">
        {ROLES.map((r) => (
          <div key={r.id} className="grid grid-cols-[4.5rem_1fr] gap-2">
            <dt className="font-medium text-foreground">{r.id}</dt>
            <dd className="leading-relaxed text-muted-foreground">{r.can}</dd>
          </div>
        ))}
      </dl>

      {removing && (
        <Dialog open onOpenChange={(isOpen) => { if (!isOpen) setRemoving(null) }}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Remove {nameOf(removing)}?</DialogTitle>
              <DialogDescription>
                {removing.userId === user?.id
                  ? 'You lose access to each project of this workspace at once. An owner must invite you again.'
                  : 'The person loses access to each project of this workspace at once. The account stays. To come back, the person needs a new invitation.'}
              </DialogDescription>
            </DialogHeader>
            {remove.isError && <p role="alert" className="text-xs text-destructive">The member was not removed. {describeError(remove.error)}</p>}
            <DialogFooter>
              <Button type="button" variant="ghost" data-autofocus onClick={() => setRemoving(null)}>Keep the member</Button>
              <Button type="button" variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate(removing)}>
                {remove.isPending ? 'Removing…' : 'Remove member'}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </>
  )
}
