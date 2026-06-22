import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { useAuth } from '@/hooks/use-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

export const Route = createFileRoute('/_auth/login')({
  component: LoginPage,
})

function LoginPage() {
  const navigate = useNavigate()
  const { login, loginError } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      await login({ email, password })
      navigate({ to: '/' })
    } catch {
      // error surfaced via loginError
    }
  }

  return (
    <div className="w-full max-w-sm rounded-lg border border-border bg-card p-6">
      <h1 className="mb-6 font-display text-2xl font-bold">Sign in to Vellum</h1>

      <div className="mb-4 space-y-2">
        <a
          href="/api/auth/external/GitHub"
          className="flex w-full items-center justify-center gap-2 rounded-md border border-border bg-background px-4 py-2 text-sm hover:bg-muted"
        >
          Sign in with GitHub
        </a>
        <a
          href="/api/auth/external/Google"
          className="flex w-full items-center justify-center gap-2 rounded-md border border-border bg-background px-4 py-2 text-sm hover:bg-muted"
        >
          Sign in with Google
        </a>
      </div>

      <div className="relative mb-4">
        <div className="absolute inset-0 flex items-center">
          <span className="w-full border-t border-border" />
        </div>
        <div className="relative flex justify-center text-xs uppercase">
          <span className="bg-card px-2 text-muted-foreground">or</span>
        </div>
      </div>

      <form onSubmit={handleSubmit} className="space-y-3">
        <Input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <Input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        {loginError && (
          <p className="text-sm text-destructive">Invalid email or password</p>
        )}
        <Button type="submit" className="w-full">
          Sign in
        </Button>
      </form>

      <p className="mt-4 text-center text-sm text-muted-foreground">
        No account?{' '}
        <a href="/register" className="text-primary hover:underline">
          Register
        </a>
      </p>
    </div>
  )
}
