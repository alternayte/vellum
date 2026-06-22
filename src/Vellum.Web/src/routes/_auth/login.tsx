import { createFileRoute } from '@tanstack/react-router'

export const Route = createFileRoute('/_auth/login')({
  component: LoginPage,
})

function LoginPage() {
  return (
    <div className="w-full max-w-sm rounded-lg border border-border bg-card p-6">
      <h1 className="mb-4 font-display text-2xl font-bold">Sign in to Vellum</h1>
      <p className="text-sm text-muted-foreground">Auth form wired in Task 6</p>
    </div>
  )
}
