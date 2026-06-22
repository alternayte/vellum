import { createFileRoute, Outlet } from '@tanstack/react-router'

export const Route = createFileRoute('/_app')({
  component: () => (
    <div className="flex h-screen flex-col bg-background text-foreground">
      <Outlet />
    </div>
  ),
})
