import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'

export const Route = createFileRoute('/_app')({
  beforeLoad: async () => {
    // A network-level failure (DNS, CORS, offline) throws here and reaches the error boundary.
    const response = await fetch('/api/auth/me')
    if (response.status === 401 || response.status === 403) {
      // A new instance has no account yet: send the first visitor to setup, not to sign-in.
      const setup = await fetch('/api/setup/status')
      if (setup.ok && (await setup.json()).needsSetup) throw redirect({ to: '/setup' })
      throw redirect({ to: '/login' })
    }
    if (!response.ok) {
      throw new Error(`Unexpected auth check failure: ${response.status}`)
    }
  },
  component: () => (
    <div className="flex h-screen flex-col bg-background text-foreground">
      <Outlet />
    </div>
  ),
})
