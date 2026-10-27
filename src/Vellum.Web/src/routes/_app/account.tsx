import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
import { ArrowLeft } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useAuth } from '@/hooks/use-auth'
import { AccountMenu } from '@/components/shell/account-menu'
import { initialsOf, useMyWorkspaces } from '@/hooks/use-my-workspaces'
import { MyInvitations } from '@/components/settings/my-invitations'

export const Route = createFileRoute('/_app/account')({
  component: AccountPage,
})

/**
 * The account of the signed-in person: the name and email Vellum knows, the workspaces the
 * person is a member of, and the open invitations. This version of Vellum has no endpoint
 * that changes the name, the email or the password, so the page shows them only.
 */
function AccountPage() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const workspaces = useMyWorkspaces()

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className="flex items-center gap-3 border-b border-border bg-card px-4 py-2.5">
        <Link to="/" className="rounded p-1 text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary" aria-label="Back to projects">
          <ArrowLeft className="h-4 w-4" />
        </Link>
        <h1 className="min-w-0 flex-1 font-heading text-base font-medium">Account</h1>
        <AccountMenu />
      </header>

      <main className="min-h-0 flex-1 overflow-y-auto px-4 py-6 md:px-8">
        <div className="mx-auto max-w-xl space-y-8">
          <section aria-labelledby="account-you" className="flex items-center gap-4">
            <span aria-hidden="true" className="flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-medium text-primary-foreground">
              {initialsOf(user?.displayName, user?.email)}
            </span>
            <dl id="account-you" className="min-w-0 space-y-1 text-sm">
              <div>
                <dt className="text-xs text-muted-foreground">Name</dt>
                <dd className="truncate text-foreground">{user?.displayName || 'No name given'}</dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">Email</dt>
                <dd className="truncate text-foreground">{user?.email}</dd>
              </div>
            </dl>
          </section>
          <p className="text-xs text-muted-foreground">
            Your name and email come from your sign-in. This version of Vellum cannot change them or your password.
          </p>

          <MyInvitations />

          <section aria-labelledby="account-workspaces">
            <h2 id="account-workspaces" className="font-heading text-base font-medium">Your workspaces</h2>
            {workspaces.isLoading ? (
              <p className="mt-2 text-sm text-muted-foreground">Loading workspaces…</p>
            ) : (workspaces.data ?? []).length === 0 ? (
              <p className="mt-2 text-sm text-muted-foreground">You are not a member of a workspace.</p>
            ) : (
              <ul className="mt-3 divide-y divide-border rounded-lg border border-border">
                {(workspaces.data ?? []).map((w) => (
                  <li key={w.id} className="flex items-center gap-2 px-3 py-2">
                    <span className="min-w-0 flex-1 truncate text-sm">{w.name}</span>
                    <Button asChild variant="ghost" size="sm" className="h-7 text-xs">
                      <Link to="/" search={{ workspace: w.id }}>Projects</Link>
                    </Button>
                    <Button asChild variant="ghost" size="sm" className="h-7 text-xs">
                      <Link to="/workspaces/$workspaceId/settings" params={{ workspaceId: w.id }} search={{}}>Settings</Link>
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <Button
            variant="outline"
            size="sm"
            onClick={async () => {
              await logout()
              navigate({ to: '/login' })
            }}
          >
            Log out
          </Button>
        </div>
      </main>
    </div>
  )
}
