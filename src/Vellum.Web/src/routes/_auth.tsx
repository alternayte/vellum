import { createFileRoute, Outlet } from '@tanstack/react-router'

// AGPL-3.0 section 13: every user of the network service gets a way to the source.
export const Route = createFileRoute('/_auth')({
  component: () => (
    <div className="flex min-h-screen flex-col items-center justify-center gap-6 bg-background">
      <Outlet />
      <p className="text-xs text-muted-foreground">
        Vellum is free software under the{' '}
        <a className="underline" href="https://www.gnu.org/licenses/agpl-3.0.html">GNU AGPL v3</a>.{' '}
        <a className="underline" href="https://github.com/alternayte/vellum">Source code</a>
      </p>
    </div>
  ),
})
