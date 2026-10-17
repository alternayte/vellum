import { createFileRoute, redirect, useNavigate } from '@tanstack/react-router'
import { useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, api, type SetupStatus } from '@/lib/settings-api'

export const Route = createFileRoute('/_auth/setup')({
  loader: async () => {
    const status = await api<SetupStatus>('GET', '/api/setup/status')
    if (!status.needsSetup) throw redirect({ to: '/login' })
    return status
  },
  component: SetupPage,
})

interface SetupResult {
  userId: string
  workspaceId: string
  projectId: string
}

/**
 * First-run setup asks for the account only. The server makes a workspace and an empty
 * project, and the app opens the project. The setup code proves access to the machine or the
 * deployment, so it stays.
 */
function SetupPage() {
  const status = Route.useLoaderData()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ email: '', password: '', setupToken: '' })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (field: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm((current) => ({ ...current, [field]: e.target.value }))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const created = await api<SetupResult>('POST', '/api/setup', {
        email: form.email,
        password: form.password,
        setupToken: status.requiresSetupToken ? form.setupToken.trim() : null,
      })
      await queryClient.invalidateQueries()
      navigate({ to: '/projects/$projectId', params: { projectId: created.projectId } })
    } catch (err) {
      const failure = err instanceof ApiError ? err : null
      setError(failure?.fields.map((f) => f.message).join(' ') || failure?.message || 'Setup did not complete.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} className="w-full max-w-md rounded-lg border border-border bg-card p-6" aria-labelledby="setup-title">
      <p className="font-mono text-[11px] uppercase tracking-wider text-muted-foreground">
        First run · {status.profile === 'hosted' ? 'Hosted' : 'Self-hosted'}
      </p>
      <h1 id="setup-title" className="mt-1 font-display text-2xl font-bold">Create your account</h1>
      <p className="mt-1.5 text-sm text-muted-foreground">
        This is the first account on this Vellum instance. It manages settings, members and keys. Vellum opens an empty project for you next.
      </p>

      <div className="mt-5 space-y-3">
        <Labelled label="Email"><Input type="email" value={form.email} onChange={set('email')} required autoFocus autoComplete="email" /></Labelled>
        <Labelled label="Password" hint="At least 10 characters, with an upper-case letter, a lower-case letter and a digit.">
          <Input type="password" value={form.password} onChange={set('password')} required minLength={10} autoComplete="new-password" />
        </Labelled>
        {status.requiresSetupToken && (
          <Labelled
            label="Setup code"
            hint={status.setupTokenHint === 'log'
              ? 'Vellum wrote a one-time code to its log when it started. Read it with `docker compose logs vellum`, or open the file `setup-code` in the data directory.'
              : 'The value of Vellum__SetupToken that the operator set for this deployment.'}
          >
            <Input className="font-mono" value={form.setupToken} onChange={set('setupToken')} required autoComplete="off" spellCheck={false} />
          </Labelled>
        )}
      </div>

      {error && <p role="alert" className="mt-3 text-sm text-destructive">{error}</p>}

      <div className="mt-5 flex justify-end">
        <Button type="submit" disabled={busy}>{busy ? 'Setting up…' : 'Create account'}</Button>
      </div>
    </form>
  )
}

function Labelled({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="block text-sm font-medium text-foreground">
      {label}
      <span className="mt-1 block">{children}</span>
      {hint && <span className="mt-1 block text-xs font-normal text-muted-foreground">{hint}</span>}
    </label>
  )
}
