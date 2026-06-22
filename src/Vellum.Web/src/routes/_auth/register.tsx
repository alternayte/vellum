import { createFileRoute } from '@tanstack/react-router'

export const Route = createFileRoute('/_auth/register')({
  component: RegisterPage,
})

function RegisterPage() {
  return (
    <div className="w-full max-w-sm rounded-lg border border-border bg-card p-6">
      <h1 className="mb-4 font-display text-2xl font-bold">Create account</h1>
      <p className="text-sm text-muted-foreground">Registration form wired in Task 6</p>
    </div>
  )
}
