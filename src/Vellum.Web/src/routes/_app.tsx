import { createFileRoute, Outlet, redirect } from '@tanstack/react-router'

export const Route = createFileRoute('/_app')({
  beforeLoad: async () => {
    try {
      const response = await fetch('/api/auth/me')
      if (!response.ok) throw new Error('Not authenticated')
    } catch {
      throw redirect({ to: '/login' })
    }
  },
  component: () => (
    <div className="flex h-screen flex-col bg-background text-foreground">
      <Outlet />
    </div>
  ),
})
