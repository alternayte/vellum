import { createFileRoute, Link } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { ArrowLeft, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import {
  ApiError, PROVIDER_PRESETS, api, countOverrides, effectiveSection, setOverride,
  type AiProvider, type AuditEntry, type InstanceInfo, type InstanceSettings, type ProviderKind,
  type SecretStatus, type SettingsDocument, type SettingsOverrides, type SettingsSection, type WorkspaceSettings,
} from '@/lib/settings-api'
import { NumberField, SectionHeading, SelectField, TextField, Toggle, ToggleField, selectClass } from '@/components/settings/fields'
import { SecretControl } from '@/components/settings/secret-control'

export const Route = createFileRoute('/_app/workspaces/$workspaceId/settings')({
  component: SettingsPage,
})

type SectionId = SettingsSection | 'audit' | 'instance'

const SECTIONS: { id: SectionId; label: string; adminOnly?: boolean }[] = [
  { id: 'ai', label: 'AI providers' },
  { id: 'github', label: 'GitHub' },
  { id: 'import', label: 'Import limits' },
  { id: 'cost', label: 'Cost' },
  { id: 'simulation', label: 'Simulation' },
  { id: 'privacy', label: 'Privacy' },
  { id: 'usage', label: 'Usage limits' },
  { id: 'notifications', label: 'Notifications' },
  { id: 'audit', label: 'Audit log' },
  { id: 'instance', label: 'Instance', adminOnly: true },
]

const TASKS: { id: keyof SettingsDocument['ai']['taskRouting']; label: string; hint: string }[] = [
  { id: 'inferArchitecture', label: 'Infer architecture', hint: 'Groups imported facts into services.' },
  { id: 'proposeFlow', label: 'Propose a flow', hint: 'Turns a request in words into a scenario proposal.' },
  { id: 'critique', label: 'Review', hint: 'Asks questions about the design.' },
  { id: 'explain', label: 'Explain', hint: 'Explains a part of the model.' },
]

function SettingsPage() {
  const { workspaceId } = Route.useParams()
  const queryClient = useQueryClient()
  const base = `/api/workspaces/${workspaceId}`

  const settings = useQuery({
    queryKey: ['workspaces', workspaceId, 'settings'],
    queryFn: () => api<WorkspaceSettings>('GET', `${base}/settings`),
  })
  const secrets = useQuery({
    queryKey: ['workspaces', workspaceId, 'secrets'],
    queryFn: () => api<SecretStatus[]>('GET', `${base}/secrets`),
    retry: false,
  })
  const instance = useQuery({ queryKey: ['instance'], queryFn: () => api<InstanceInfo>('GET', '/api/instance') })

  const [section, setSection] = useState<SectionId>('ai')
  // Edits live in `edited` until they are saved; without edits the form shows what is stored.
  const [edited, setEdited] = useState<SettingsOverrides | null>(null)
  const saved = settings.data?.overrides
  const draft = edited ?? saved ?? null
  const setDraft = (change: (current: SettingsOverrides) => SettingsOverrides) =>
    setEdited((current) => change(current ?? saved ?? {}))

  const save = useMutation({
    mutationFn: (overrides: SettingsOverrides) => api<WorkspaceSettings>('PUT', `${base}/settings`, overrides),
    onSuccess: (data) => {
      queryClient.setQueryData(['workspaces', workspaceId, 'settings'], data)
      queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'audit'] })
      setEdited(null)
    },
    onError: shownInline,
  })

  const dirty = useMemo(() => edited !== null && JSON.stringify(edited) !== JSON.stringify(saved ?? {}), [edited, saved])
  const fieldErrors = save.error instanceof ApiError ? save.error.fields : []
  const errorFor = (path: string) => fieldErrors.find((e) => e.field.toLowerCase() === path.toLowerCase())?.message
  // Secrets are readable only by a settings admin, so access to them tells us the role.
  const canEdit = secrets.isSuccess
  const visibleSections = SECTIONS.filter((s) => !s.adminOnly || instance.data?.isInstanceAdmin)

  if (settings.isLoading || !draft || !settings.data) {
    return (
      <main className="flex flex-1 items-center justify-center text-sm text-muted-foreground">
        {settings.isError ? 'Settings did not load. You may not be a member of this workspace.' : 'Loading settings…'}
      </main>
    )
  }

  const data = settings.data
  const current = <S extends SettingsSection>(s: S) => effectiveSection(data, draft, s)
  const bind = <S extends SettingsSection, K extends keyof SettingsDocument[S]>(s: S, field: K) => ({
    value: current(s)[field],
    onChange: (value: SettingsDocument[S][K]) => setDraft((d) => setOverride(d, s, field, value)),
    overridden: draft[s] !== undefined && field in (draft[s] as object),
    onReset: () => setDraft((d) => setOverride(d, s, field, undefined)),
    error: errorFor(`${s}.${String(field)}`),
  })
  const secretFor = (name: string) => secrets.data?.find((s) => s.name === name)

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <header className="flex items-center gap-3 border-b border-border bg-card px-4 py-2.5">
        <Link to="/" className="rounded p-1 text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary" aria-label="Back to workspaces">
          <ArrowLeft className="h-4 w-4" />
        </Link>
        <h1 className="font-heading text-base font-medium">Workspace settings</h1>
        <span className="text-xs text-muted-foreground">
          {countOverrides(draft)} {countOverrides(draft) === 1 ? 'value differs' : 'values differ'} from the instance defaults
        </span>
      </header>

      <div className="flex min-h-0 flex-1 flex-col md:flex-row">
        <nav aria-label="Settings sections" className="shrink-0 border-b border-border p-2 md:w-52 md:border-b-0 md:border-r">
          <ul className="flex gap-1 overflow-x-auto md:flex-col">
            {visibleSections.map((s) => (
              <li key={s.id}>
                <button
                  type="button"
                  aria-current={section === s.id ? 'page' : undefined}
                  onClick={() => setSection(s.id)}
                  className={cn(
                    'w-full whitespace-nowrap rounded-md px-3 py-1.5 text-left text-sm text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                    section === s.id && 'bg-muted text-foreground',
                  )}
                >
                  {s.label}
                </button>
              </li>
            ))}
          </ul>
        </nav>

        <main className="min-w-0 flex-1 overflow-y-auto px-4 py-6 pb-28 md:px-8">
          <div className="mx-auto max-w-3xl">
            {section === 'ai' && (
              <>
                <SectionHeading title="AI providers" description="Vellum works without an AI provider. Add one to get proposals, reviews and explanations. Each task uses the provider you select for it." />
                <ProviderList
                  providers={current('ai').providers}
                  overridden={draft.ai?.providers !== undefined}
                  onChange={(providers) => setDraft((d) => setOverride(d, 'ai', 'providers', providers))}
                  onReset={() => setDraft((d) => setOverride(d, 'ai', 'providers', undefined))}
                  errorFor={errorFor}
                  renderSecret={(provider) => (
                    <SecretControl workspaceId={workspaceId} name={`ai:${provider.id}:apiKey`} label="API key" status={secretFor(`ai:${provider.id}:apiKey`)} canEdit={canEdit} />
                  )}
                />
                <h3 className="mt-8 text-sm font-medium text-foreground">Task routing</h3>
                {TASKS.map((task) => (
                  <SelectField
                    key={task.id}
                    label={task.label}
                    hint={task.hint}
                    value={current('ai').taskRouting[task.id] ?? ''}
                    onChange={(id) => setDraft((d) => setOverride(d, 'ai', 'taskRouting', { ...current('ai').taskRouting, [task.id]: id || null }))}
                    overridden={draft.ai?.taskRouting !== undefined}
                    onReset={() => setDraft((d) => setOverride(d, 'ai', 'taskRouting', undefined))}
                    error={errorFor(`ai.taskRouting.${task.id}`)}
                    options={[
                      { value: '', label: 'Off: this task does not use AI' },
                      ...current('ai').providers.filter((p) => p.enabled).map((p) => ({ value: p.id, label: `${p.name || PROVIDER_PRESETS[p.kind].label} · ${p.model || 'no model'}` })),
                    ]}
                  />
                ))}
              </>
            )}

            {section === 'github' && (
              <>
                <SectionHeading title="GitHub" description="Vellum reads repositories and never writes to them. A public repository needs no token." />
                <TextField label="API base URL" hint="Change this only for GitHub Enterprise Server." {...bind('github', 'apiBaseUrl')} />
                <TextField label="Default ref" hint="The branch or tag an import reads when you give none. Empty uses the default branch of the repository." {...bind('github', 'defaultRef')} />
                <div className="mt-4">
                  <SecretControl workspaceId={workspaceId} name="github:token" label="Fine-grained access token (read-only)" status={secretFor('github:token')} canEdit={canEdit} />
                </div>
              </>
            )}

            {section === 'import' && (
              <>
                <SectionHeading title="Import limits" description="An import stops at these limits and reports what it left out. It never describes a partial scan as complete." />
                <NumberField label="Files per import" min={1} {...bind('import', 'maxFiles')} />
                <NumberField label="Largest file" min={1} suffix="bytes" {...bind('import', 'maxFileBytes')} />
                <NumberField label="Total size" min={1} suffix="bytes" {...bind('import', 'maxTotalBytes')} />
                <TextField
                  label="Excluded paths"
                  hint="Glob patterns, separated by commas."
                  value={current('import').excludeGlobs.join(', ')}
                  onChange={(text) => setDraft((d) => setOverride(d, 'import', 'excludeGlobs', text.split(',').map((g) => g.trim()).filter(Boolean)))}
                  overridden={draft.import?.excludeGlobs !== undefined}
                  onReset={() => setDraft((d) => setOverride(d, 'import', 'excludeGlobs', undefined))}
                  error={errorFor('import.excludeGlobs')}
                />
              </>
            )}

            {section === 'cost' && (
              <>
                <SectionHeading title="Cost" description="The defaults for a new cost estimate. A resource with no supported price shows as unknown, never as zero." />
                <TextField label="Cloud provider" placeholder="aws, azure, gcp or custom" {...bind('cost', 'provider')} />
                <TextField label="Region" placeholder="eu-west-1" {...bind('cost', 'region')} />
                <TextField label="Currency" hint="A three-letter code, such as USD or GBP." {...bind('cost', 'currency')} />
              </>
            )}

            {section === 'simulation' && (
              <>
                <SectionHeading title="Simulation" description="The defaults for a new stress model. A run records its seed, so the same seed gives the same result." />
                <NumberField label="Seed" min={0} {...bind('simulation', 'seed')} />
                <NumberField label="Duration" min={1} suffix="seconds of model time" {...bind('simulation', 'durationSeconds')} />
                <NumberField label="Arrival rate" min={0} step={0.1} suffix="requests per second" {...bind('simulation', 'arrivalRatePerSecond')} />
              </>
            )}

            {section === 'privacy' && (
              <>
                <SectionHeading title="Privacy" description="Secrets are always removed from text before it goes to an AI provider. This setting controls source code from private repositories." />
                <ToggleField
                  label="Send private repository code to AI providers"
                  hint="Off by default. With this off, AI features see only the model and public sources."
                  {...bind('privacy', 'sendPrivateCodeToAi')}
                />
              </>
            )}

            {section === 'usage' && (
              <>
                <SectionHeading title="Usage limits" description="Vellum stops a task that would pass a limit and says which limit it reached." />
                <NumberField label="AI budget per month" min={0} step={1} nullable suffix="USD" {...bind('usage', 'monthlyAiBudgetUsd')} />
                <NumberField label="Repository scans per day" min={0} {...bind('usage', 'maxScansPerDay')} />
              </>
            )}

            {section === 'notifications' && (
              <>
                <SectionHeading title="Notifications" description="Vellum can post an event to a webhook when an import or a review finishes." />
                <TextField
                  label="Webhook URL"
                  placeholder="https://example.com/hooks/vellum"
                  value={current('notifications').webhookUrl ?? ''}
                  onChange={(url) => setDraft((d) => setOverride(d, 'notifications', 'webhookUrl', url || null))}
                  overridden={draft.notifications?.webhookUrl !== undefined}
                  onReset={() => setDraft((d) => setOverride(d, 'notifications', 'webhookUrl', undefined))}
                  error={errorFor('notifications.webhookUrl')}
                />
                <div className="mt-4">
                  <SecretControl workspaceId={workspaceId} name="notifications:webhookSecret" label="Signing secret" status={secretFor('notifications:webhookSecret')} canEdit={canEdit} />
                </div>
              </>
            )}

            {section === 'audit' && <AuditLog workspaceId={workspaceId} />}
            {section === 'instance' && instance.data && <InstancePanel info={instance.data} />}
          </div>
        </main>
      </div>

      {(dirty || save.isError) && section !== 'audit' && section !== 'instance' && (
        <div role="region" aria-label="Unsaved changes" className="fixed inset-x-0 bottom-0 z-20 border-t border-border bg-card/95 px-4 py-3 backdrop-blur">
          <div className="mx-auto flex max-w-3xl flex-wrap items-center gap-3">
            <p className={cn('min-w-0 flex-1 text-sm', save.isError ? 'text-destructive' : 'text-foreground')} role={save.isError ? 'alert' : undefined}>
              {save.isError
                ? fieldErrors.length > 0
                  ? `${fieldErrors.length} ${fieldErrors.length === 1 ? 'value was' : 'values were'} refused. ${fieldErrors[0].message}`
                  : save.error.message
                : 'You have unsaved changes.'}
            </p>
            <Button variant="ghost" size="sm" onClick={() => { setEdited(null); save.reset() }}>Discard</Button>
            <Button size="sm" disabled={save.isPending || !dirty} onClick={() => save.mutate(draft)}>
              {save.isPending ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}

function ProviderList({
  providers, overridden, onChange, onReset, errorFor, renderSecret,
}: {
  providers: AiProvider[]
  overridden: boolean
  onChange: (providers: AiProvider[]) => void
  onReset: () => void
  errorFor: (path: string) => string | undefined
  renderSecret: (provider: AiProvider) => React.ReactNode
}) {
  const update = (index: number, patch: Partial<AiProvider>) =>
    onChange(providers.map((p, i) => (i === index ? { ...p, ...patch } : p)))
  const add = (kind: ProviderKind) =>
    onChange([...providers, {
      id: crypto.randomUUID(), kind, name: PROVIDER_PRESETS[kind].label.split(' (')[0], baseUrl: PROVIDER_PRESETS[kind].baseUrl,
      model: '', enabled: true, supportsStructuredOutput: kind !== 'openai-compatible', supportsStreaming: true,
      temperature: null, timeoutSeconds: 60, monthlyBudgetUsd: null, requestsPerMinute: null,
    }])

  return (
    <div className="mt-4 space-y-4">
      {providers.length === 0 && (
        <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
          No provider yet. Modelling, tracing, review rules and export work without one.
        </p>
      )}
      {providers.map((provider, index) => (
        <fieldset key={provider.id} className="rounded-lg border border-border p-4">
          <legend className="sr-only">{provider.name || 'Provider'}</legend>
          <div className="flex flex-wrap items-center gap-3">
            <Input
              className="max-w-64 font-medium"
              value={provider.name}
              onChange={(e) => update(index, { name: e.target.value })}
              aria-label="Provider name"
            />
            <span className="rounded-full border border-border px-2 py-0.5 text-xs text-muted-foreground">{PROVIDER_PRESETS[provider.kind].label.split(' (')[0]}</span>
            <div className="flex-1" />
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              Enabled
              <Toggle checked={provider.enabled} onChange={(enabled) => update(index, { enabled })} label={`Enable ${provider.name}`} />
            </label>
            <Button variant="ghost" size="icon" className="h-7 w-7 text-muted-foreground hover:text-destructive" onClick={() => onChange(providers.filter((_, i) => i !== index))} aria-label={`Remove ${provider.name}`}>
              <Trash2 className="h-4 w-4" />
            </Button>
          </div>

          <div className="mt-3 grid gap-3 sm:grid-cols-2">
            <LabelledInput label="Base URL" value={provider.baseUrl} onChange={(baseUrl) => update(index, { baseUrl })} error={errorFor(`ai.providers[${index}].baseUrl`)} />
            <LabelledInput label="Model" value={provider.model} onChange={(model) => update(index, { model })} placeholder="The model name the provider expects" error={errorFor(`ai.providers[${index}].model`)} />
            <LabelledInput label="Timeout (seconds)" type="number" value={String(provider.timeoutSeconds)} onChange={(v) => update(index, { timeoutSeconds: Number(v) || 0 })} error={errorFor(`ai.providers[${index}].timeoutSeconds`)} />
            <LabelledInput label="Temperature" type="number" value={provider.temperature === null ? '' : String(provider.temperature)} placeholder="Provider default" onChange={(v) => update(index, { temperature: v === '' ? null : Number(v) })} error={errorFor(`ai.providers[${index}].temperature`)} />
            <LabelledInput label="Budget per month (USD)" type="number" value={provider.monthlyBudgetUsd === null ? '' : String(provider.monthlyBudgetUsd)} placeholder="No limit" onChange={(v) => update(index, { monthlyBudgetUsd: v === '' ? null : Number(v) })} error={errorFor(`ai.providers[${index}].monthlyBudgetUsd`)} />
            <LabelledInput label="Requests per minute" type="number" value={provider.requestsPerMinute === null ? '' : String(provider.requestsPerMinute)} placeholder="No limit" onChange={(v) => update(index, { requestsPerMinute: v === '' ? null : Number(v) })} error={errorFor(`ai.providers[${index}].requestsPerMinute`)} />
          </div>

          <div className="mt-3 flex flex-wrap gap-x-6 gap-y-2 text-xs text-muted-foreground">
            <label className="flex items-center gap-2">
              <Toggle checked={provider.supportsStructuredOutput} onChange={(v) => update(index, { supportsStructuredOutput: v })} label="Structured output" />
              Structured output
            </label>
            <label className="flex items-center gap-2">
              <Toggle checked={provider.supportsStreaming} onChange={(v) => update(index, { supportsStreaming: v })} label="Streaming" />
              Streaming
            </label>
          </div>
          <p className="mt-1.5 text-xs text-muted-foreground">Turn a capability off when the endpoint does not support it. Vellum then validates the reply itself.</p>

          <div className="mt-3">{renderSecret(provider)}</div>
        </fieldset>
      ))}

      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="add-provider" className="sr-only">Add a provider</label>
        <select
          id="add-provider"
          className={cn(selectClass, 'max-w-80')}
          value=""
          onChange={(e) => { if (e.target.value) add(e.target.value as ProviderKind) }}
        >
          <option value="">Add a provider…</option>
          {(Object.keys(PROVIDER_PRESETS) as ProviderKind[]).map((kind) => <option key={kind} value={kind}>{PROVIDER_PRESETS[kind].label}</option>)}
        </select>
        <Plus className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
        <span className="text-[11px] text-muted-foreground">{overridden ? 'This list is set for this workspace.' : 'This list is the instance default.'}</span>
        {overridden && (
          <button type="button" onClick={onReset} className="rounded text-[11px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary">
            Use the default list
          </button>
        )}
      </div>
      {errorFor('ai.providers') && <p role="alert" className="text-xs text-destructive">{errorFor('ai.providers')}</p>}
    </div>
  )
}

function LabelledInput({ label, value, onChange, error, ...input }: { label: string; value: string; onChange: (value: string) => void; error?: string; type?: string; placeholder?: string }) {
  return (
    <label className="block text-xs font-medium text-muted-foreground">
      {label}
      <Input className="mt-1" value={value} aria-invalid={error ? true : undefined} onChange={(e) => onChange(e.target.value)} {...input} />
      {error && <span role="alert" className="mt-1 block font-normal text-destructive">{error}</span>}
    </label>
  )
}

function AuditLog({ workspaceId }: { workspaceId: string }) {
  const audit = useQuery({
    queryKey: ['workspaces', workspaceId, 'audit'],
    queryFn: () => api<AuditEntry[]>('GET', `/api/workspaces/${workspaceId}/audit?limit=100`),
    retry: false,
  })
  return (
    <>
      <SectionHeading title="Audit log" description="Changes to settings, credentials, keys and roles. The log records who changed what, never the values." />
      {audit.isError && <p className="mt-4 text-sm text-muted-foreground">Only a workspace administrator can read the audit log.</p>}
      {audit.data?.length === 0 && <p className="mt-4 text-sm text-muted-foreground">Nothing has changed yet.</p>}
      {audit.data && audit.data.length > 0 && (
        <div className="mt-4 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr><th className="px-3 py-2 font-medium">When</th><th className="px-3 py-2 font-medium">Action</th><th className="px-3 py-2 font-medium">Target</th><th className="px-3 py-2 font-medium">By</th></tr>
            </thead>
            <tbody>
              {audit.data.map((entry, i) => (
                <tr key={i} className="border-b border-border last:border-b-0">
                  <td className="whitespace-nowrap px-3 py-2 text-muted-foreground">{new Date(entry.at).toLocaleString()}</td>
                  <td className="px-3 py-2">{entry.action}</td>
                  <td className="px-3 py-2 font-mono text-xs">{entry.target}</td>
                  <td className="px-3 py-2 font-mono text-xs text-muted-foreground">{entry.actorId.slice(0, 8)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

function InstancePanel({ info }: { info: InstanceInfo }) {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: ['instance', 'settings'], queryFn: () => api<InstanceSettings>('GET', '/api/instance/settings') })
  const diagnostics = useQuery({ queryKey: ['instance', 'diagnostics'], queryFn: () => api<Record<string, unknown>>('GET', '/api/instance/diagnostics') })
  const savePolicy = useMutation({
    mutationFn: (next: InstanceSettings) => api<InstanceSettings>('PUT', '/api/instance/settings', next),
    onSuccess: (data) => {
      queryClient.setQueryData(['instance', 'settings'], data)
      queryClient.invalidateQueries({ queryKey: ['instance'] })
      queryClient.invalidateQueries({ queryKey: ['workspaces'] })
    },
    onError: shownInline,
  })
  const rotate = useMutation({ mutationFn: () => api<{ rewrapped: number }>('POST', '/api/instance/keys/rotate'), onError: shownInline })
  const policy = settings.data?.policy
  const setPolicy = (patch: Partial<InstanceSettings['policy']>) => {
    if (settings.data) savePolicy.mutate({ ...settings.data, policy: { ...settings.data.policy, ...patch } })
  }

  return (
    <>
      <SectionHeading title="Instance" description={`This Vellum instance runs the ${info.profile === 'hosted' ? 'hosted' : 'self-hosted'} profile, version ${info.version}. These policies apply to every workspace.`} />
      {policy && (
        <>
          <ToggleField label="Let people register themselves" hint="With this off, only an administrator adds accounts." value={policy.allowSelfRegistration} onChange={(v) => setPolicy({ allowSelfRegistration: v })} />
          <ToggleField label="Allow URLs on private networks" hint="Needed for a local model server such as Ollama. Keep it off when people you do not know can change settings." value={policy.allowPrivateNetworkUrls} onChange={(v) => setPolicy({ allowPrivateNetworkUrls: v })} />
          <ToggleField label="Let workspaces send private code to AI" hint="A workspace must also turn this on for itself." value={policy.allowSendingPrivateCodeToAi} onChange={(v) => setPolicy({ allowSendingPrivateCodeToAi: v })} />
        </>
      )}
      {savePolicy.isError && <p role="alert" className="mt-2 text-sm text-destructive">{savePolicy.error.message}</p>}

      <h3 className="mt-8 text-sm font-medium text-foreground">Encryption key</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        To change the master key, start Vellum with the new key and the old key as the previous key. Then re-encrypt. After that, remove the previous key.
      </p>
      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Button variant="secondary" size="sm" disabled={rotate.isPending} onClick={() => rotate.mutate()}>
          {rotate.isPending ? 'Re-encrypting…' : 'Re-encrypt with the current key'}
        </Button>
        {rotate.isSuccess && <span className="text-xs text-muted-foreground" role="status">{rotate.data.rewrapped} stored keys now use the current key.</span>}
        {rotate.isError && <span className="text-xs text-destructive" role="alert">{rotate.error.message}</span>}
      </div>

      <h3 className="mt-8 text-sm font-medium text-foreground">Diagnostics</h3>
      {diagnostics.data && (
        <pre tabIndex={0} className="mt-2 max-h-96 overflow-auto rounded-lg border border-border bg-background p-3 font-mono text-xs leading-relaxed focus-visible:outline-2 focus-visible:outline-primary">
          {JSON.stringify(diagnostics.data, null, 2)}
        </pre>
      )}
      {diagnostics.isError && <p className="mt-2 text-sm text-destructive">{diagnostics.error.message}</p>}
    </>
  )
}
