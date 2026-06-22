import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'

export const Route = createFileRoute('/_app')({
  beforeLoad: async () => {
    let response: Response
    try {
      response = await fetch('/api/auth/me')
    } catch (err) {
      // Network-level failure (DNS, CORS, offline) — let the error boundary handle it
      throw err
    }
    if (response.status === 401 || response.status === 403) {
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
