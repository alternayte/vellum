import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { useAuth } from '@/hooks/use-auth'
import { useSignInMethods } from '@/hooks/use-sign-in-methods'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { pendingInvitation } from '@/lib/invitation-link'

export const Route = createFileRoute('/_auth/register')({
  component: RegisterPage,
})

function RegisterPage() {
  const navigate = useNavigate()
  const { register, registerError } = useAuth()
  const methods = useSignInMethods()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      const created = await register({ email, password, displayName: displayName || undefined })
      // A sign-up that started at an invitation link goes on to the link from the start page.
      if (pendingInvitation()) navigate({ to: '/' })
      else navigate({ to: '/projects/$projectId', params: { projectId: created.projectId } })
    } catch {
      // error surfaced via registerError
    }
  }

  // A form that the server refuses for everyone is not shown.
  if (methods.data && !methods.data.registrationOpen) {
    return (
      <div className="w-full max-w-sm rounded-lg border border-border bg-card p-6">
        <h1 className="mb-3 font-display text-2xl font-bold">Registration is closed</h1>
        <p className="text-sm text-muted-foreground">
          This instance does not let people create an account. Ask its administrator for an account.
        </p>
        <p className="mt-4 text-sm">
          <a href="/login" className="text-primary hover:underline">Sign in</a>
        </p>
      </div>
    )
  }

  return (
    <div className="w-full max-w-sm rounded-lg border border-border bg-card p-6">
      <h1 className="mb-6 font-display text-2xl font-bold">Create account</h1>

      <form onSubmit={handleSubmit} className="space-y-3">
        <Input
          type="text"
          aria-label="Display name (optional)"
          placeholder="Display name (optional)"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
        />
        <Input
          type="email"
          aria-label="Email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <Input
          type="password"
          aria-label="Password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        {registerError && (
          <p className="text-sm text-destructive">Registration failed. Please try again.</p>
        )}
        <Button type="submit" className="w-full">
          Create account
        </Button>
      </form>

      <p className="mt-4 text-center text-sm text-muted-foreground">
        Already have an account?{' '}
        <a href="/login" className="text-primary hover:underline">
          Sign in
        </a>
      </p>
    </div>
  )
}
