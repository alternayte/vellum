import { useId, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ShieldAlert, ShieldCheck, TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { loadTestsApi, TARGET_ENVIRONMENTS, type LoadTestTarget } from '@/lib/load-tests-api'

interface Props {
  projectId: string
  /** Null for a new target. */
  existing: LoadTestTarget | null
  onClose: () => void
  /** Called after each stored change with the target as it is now, or null when it was removed. */
  onChanged: (target: LoadTestTarget | null) => void
}

const selectClass =
  'mt-1 h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30'

function FieldError({ id, errors, field }: { id: string; errors: { field: string; message: string }[]; field: string }) {
  const error = errors.find((e) => e.field === field)
  return error ? <p id={id} role="alert" className="mt-1 text-[11px] text-[var(--trace-failure)]">{error.message}</p> : null
}

/**
 * One load-test target: its name, base URL and class, the authorisation of an owner, and
 * for a production-class target the override of the production defaults.
 */
export function TargetDialog({ projectId, existing, onClose, onChanged }: Props) {
  const ids = useId()
  const [target, setTarget] = useState(existing)
  const [name, setName] = useState(existing?.name ?? '')
  const [baseUrl, setBaseUrl] = useState(existing?.baseUrl ?? '')
  // A new target has no class until a person chooses one. Production is never chosen for them.
  const [environment, setEnvironment] = useState<string>(existing?.environment ?? '')
  const [errors, setErrors] = useState<{ field: string; message: string }[]>([])
  const [failure, setFailure] = useState<string | null>(null)

  const [allowNonIdempotent, setAllowNonIdempotent] = useState(existing?.override?.allowNonIdempotent ?? false)
  const [allowFailureInjection, setAllowFailureInjection] = useState(existing?.override?.allowFailureInjection ?? false)
  const [reason, setReason] = useState(existing?.override?.reason ?? '')
  const [confirmName, setConfirmName] = useState('')

  const stored = (next: LoadTestTarget) => {
    setTarget(next)
    setName(next.name)
    setBaseUrl(next.baseUrl)
    setEnvironment(next.environment)
    setAllowNonIdempotent(next.override?.allowNonIdempotent ?? false)
    setAllowFailureInjection(next.override?.allowFailureInjection ?? false)
    setReason(next.override?.reason ?? '')
    setConfirmName('')
    onChanged(next)
  }
  const failed = (e: Error) => {
    setErrors(e instanceof ApiError ? e.fields : [])
    setFailure(e instanceof ApiError && e.fields.length > 0 ? null : e.message)
    shownInline()
  }
  const start = () => { setErrors([]); setFailure(null) }

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), baseUrl: baseUrl.trim(), environment }
      return target ? loadTestsApi.changeTarget(projectId, target.id, body) : loadTestsApi.addTarget(projectId, body)
    },
    onMutate: start, onSuccess: stored, onError: failed,
  })
  // The request names what this dialog shows: the base URL, the class and the version. When the target changed
  // since, the server authorises nothing (409). The dialog then reads the target again, so the person sees what it holds now.
  const authorise = useMutation({
    mutationFn: () => loadTestsApi.authorise(projectId, target!),
    onMutate: start, onSuccess: stored,
    onError: async (e: Error) => {
      failed(e)
      if (!(e instanceof ApiError) || e.status !== 409) return
      try {
        const current = (await loadTestsApi.targets(projectId)).find((t) => t.id === target!.id)
        if (current) {
          stored(current)
          setFailure('The target changed after this dialog read it. Nothing was authorised. Read the base URL and the class below, then authorise again.')
        } else {
          setFailure('The target was removed. Nothing was authorised.')
        }
      } catch {
        // The first message stays: the target could not be read again.
      }
    },
  })
  const revoke = useMutation({
    mutationFn: () => loadTestsApi.revoke(projectId, target!.id),
    onMutate: start, onSuccess: stored, onError: failed,
  })
  const override = useMutation({
    mutationFn: (clear: boolean) => loadTestsApi.setOverride(projectId, target!.id, clear
      ? { allowNonIdempotent: false, allowFailureInjection: false, reason: '', confirmName: '' }
      : { allowNonIdempotent, allowFailureInjection, reason, confirmName }),
    onMutate: start, onSuccess: stored, onError: failed,
  })
  const remove = useMutation({
    mutationFn: () => loadTestsApi.removeTarget(projectId, target!.id),
    onMutate: start, onSuccess: () => { onChanged(null); onClose() }, onError: failed,
  })

  const busy = save.isPending || authorise.isPending || revoke.isPending || override.isPending || remove.isPending
  const changed = target !== null && (baseUrl.trim() !== target.baseUrl || environment !== target.environment)
  const dirty = target === null || changed || name.trim() !== target.name

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[94vh] flex-col gap-4 sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{target ? target.name : 'New load-test target'}</DialogTitle>
          <DialogDescription>
            A target is the base URL of a deployed system that you run k6 against. Vellum sends no load: it writes the script, and you run it.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          <form
            className="space-y-3"
            onSubmit={(e) => { e.preventDefault(); save.mutate() }}
          >
            <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-name`}>
              Name
              <Input id={`${ids}-name`} className="mt-1" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} aria-describedby={`${ids}-name-error`} />
            </label>
            <FieldError id={`${ids}-name-error`} errors={errors} field="name" />

            <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-url`}>
              Base URL
              <Input
                id={`${ids}-url`} className="mt-1 font-mono" value={baseUrl} placeholder="https://orders.test.example" spellCheck={false}
                onChange={(e) => setBaseUrl(e.target.value)} aria-describedby={`${ids}-url-error ${ids}-url-hint`}
              />
            </label>
            <p id={`${ids}-url-hint`} className="text-[11px] leading-relaxed text-muted-foreground">
              No credentials, no query. The script adds the path of each operation. Credentials go to k6 in an environment variable.
            </p>
            <FieldError id={`${ids}-url-error`} errors={errors} field="baseUrl" />

            <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-class`}>
              Class of the system
              <select id={`${ids}-class`} className={selectClass} value={environment} onChange={(e) => setEnvironment(e.target.value)} aria-describedby={`${ids}-class-error`}>
                <option value="" disabled>Choose a class</option>
                {TARGET_ENVIRONMENTS.map((e) => <option key={e} value={e}>{e}</option>)}
              </select>
            </label>
            <FieldError id={`${ids}-class-error`} errors={errors} field="environment" />
            {environment === 'production' && (
              <p className="flex gap-2 rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs leading-relaxed text-foreground">
                <TriangleAlert aria-hidden className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--trace-failure)]" />
                <span>A script for a production-class target leaves out every POST, PUT, PATCH and DELETE request, and a scenario with faults gets no script. An owner can override this below.</span>
              </p>
            )}
            {changed && target?.authorisation && (
              <p role="status" className="text-xs text-[var(--trace-retry)]">A new base URL or class voids the authorisation{target.override ? ' and clears the override' : ''}. An owner authorises the target again.</p>
            )}

            <div className="flex items-center gap-2">
              <Button type="submit" size="sm" disabled={busy || !dirty || name.trim() === '' || baseUrl.trim() === '' || environment === ''}>
                {save.isPending ? 'Saving…' : target ? 'Save changes' : 'Add target'}
              </Button>
              {target && (
                <Button
                  type="button" variant="ghost" size="sm" className="text-muted-foreground" disabled={busy}
                  onClick={() => { if (window.confirm(`Remove “${target.name}”? Its scripts and measured results stay.`)) remove.mutate() }}
                >
                  Remove target
                </Button>
              )}
            </div>
          </form>

          {failure && <p role="alert" className="rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs text-[var(--trace-failure)]">{failure}</p>}

          {target && (
            <section aria-labelledby={`${ids}-auth`} className="space-y-2 border-t border-border pt-4">
              <h3 id={`${ids}-auth`} className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Authorisation</h3>
              {target.authorisation ? (
                <div className="flex gap-2 rounded-md border border-[var(--trace-request)]/60 px-3 py-2 text-xs text-foreground">
                  <ShieldCheck aria-hidden className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--trace-request)]" />
                  <p>
                    Authorised for <span className="font-mono">{target.authorisation.baseUrl}</span> as {target.authorisation.environment}, on {new Date(target.authorisation.at).toLocaleString()}.
                    <span className="mt-0.5 block text-[11px] text-muted-foreground">By user {target.authorisation.by}</span>
                  </p>
                </div>
              ) : (
                <div className="flex gap-2 rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
                  <ShieldAlert aria-hidden className="mt-0.5 h-3.5 w-3.5 shrink-0 text-[var(--trace-retry)]" />
                  <p>Not authorised. No script is generated for this target, and no result is accepted for it.</p>
                </div>
              )}
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                An owner of the workspace authorises a target. An agent token cannot do this.
              </p>
              {!target.authorisation && (
                <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 rounded-md border border-border px-3 py-2 text-xs text-foreground">
                  <dt className="col-span-2 mb-0.5 text-[11px] font-medium text-muted-foreground">You authorise k6 load against:</dt>
                  <dt className="text-muted-foreground">Base URL</dt><dd className="break-all font-mono">{target.baseUrl}</dd>
                  <dt className="text-muted-foreground">Class</dt><dd className={target.environment === 'production' ? 'font-semibold text-[var(--trace-failure)]' : ''}>{target.environment}</dd>
                  <dt className="text-muted-foreground">Version</dt><dd className="font-mono">{target.version}</dd>
                </dl>
              )}
              <div className="flex gap-2">
                {!target.authorisation && (
                  <Button type="button" size="sm" disabled={busy || dirty} title={dirty ? 'Save the changes first' : undefined} onClick={() => authorise.mutate()}>
                    {authorise.isPending ? 'Authorising…' : `Authorise as ${target.environment}`}
                  </Button>
                )}
                {target.authorisation && (
                  <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => revoke.mutate()}>
                    {revoke.isPending ? 'Revoking…' : 'Revoke the authorisation'}
                  </Button>
                )}
              </div>
            </section>
          )}

          {target?.environment === 'production' && (
            <section aria-labelledby={`${ids}-override`} className="space-y-3 rounded-md border border-[var(--trace-failure)]/70 px-3 py-3">
              <h3 id={`${ids}-override`} className="flex items-center gap-2 text-xs font-medium uppercase tracking-wider text-[var(--trace-failure)]">
                <TriangleAlert aria-hidden className="h-3.5 w-3.5" /> Production override
              </h3>
              <p className="text-xs leading-relaxed text-foreground">
                With an override, a script sends requests that change data in a production system, at the rate of the stress profile.
                Set it only for a load test that the owner of that system agreed to. The override is in the audit log and in the header of each script.
              </p>
              {target.override && (
                <p role="status" className="text-xs text-foreground">
                  An override is set, on {new Date(target.override.at).toLocaleString()}, by user {target.override.by}: {target.override.reason}
                </p>
              )}
              <label className="flex items-start gap-2 text-xs text-foreground">
                <input type="checkbox" className="mt-0.5" checked={allowNonIdempotent} onChange={(e) => setAllowNonIdempotent(e.target.checked)} />
                <span>Allow non-idempotent operations: POST, PUT, PATCH and DELETE requests</span>
              </label>
              <label className="flex items-start gap-2 text-xs text-foreground">
                <input type="checkbox" className="mt-0.5" checked={allowFailureInjection} onChange={(e) => setAllowFailureInjection(e.target.checked)} />
                <span>Allow failure injection: scripts for a scenario with faults</span>
              </label>
              <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-reason`}>
                Reason
                <Input id={`${ids}-reason`} className="mt-1" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} aria-describedby={`${ids}-reason-error`} />
              </label>
              <FieldError id={`${ids}-reason-error`} errors={errors} field="reason" />
              <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-confirm`}>
                Type the name of the target to confirm: <span className="font-mono text-foreground">{target.name}</span>
                <Input id={`${ids}-confirm`} className="mt-1" value={confirmName} autoComplete="off" onChange={(e) => setConfirmName(e.target.value)} aria-describedby={`${ids}-confirm-error`} />
              </label>
              <FieldError id={`${ids}-confirm-error`} errors={errors} field="confirmName" />
              <FieldError id={`${ids}-environment-error`} errors={errors} field="environment" />
              <div className="flex gap-2">
                <Button
                  type="button" variant="destructive" size="sm"
                  disabled={busy || dirty || (!allowNonIdempotent && !allowFailureInjection) || reason.trim() === '' || confirmName !== target.name}
                  onClick={() => override.mutate(false)}
                >
                  {override.isPending ? 'Saving…' : 'Set the override'}
                </Button>
                {target.override && (
                  <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => override.mutate(true)}>Clear the override</Button>
                )}
              </div>
            </section>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
