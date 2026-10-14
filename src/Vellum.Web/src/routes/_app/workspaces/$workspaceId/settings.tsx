import { createFileRoute, Link } from '@tanstack/react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useMemo, useState } from 'react'
import { ArrowLeft, CircleCheck, CircleX, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import {
  ApiError, PROVIDER_PRESETS, api, countOverrides, effectiveSection, setOverride,
  type AiProvider, type AiUsage, type AuditEntry, type GitHubCredential, type GitHubTestResult, type InstanceInfo, type InstanceSettings,
  type InstanceDiagnostics, type ModelPrice, type ProviderKind, type ProviderTestResult, type RotateKeysResult,
  type SecretStatus, type SettingsDocument, type SettingsOverrides, type SettingsSection, type WorkspaceSettings,
} from '@/lib/settings-api'
import { NumberField, SectionHeading, SelectField, TextField, Toggle, ToggleField, selectClass } from '@/components/settings/fields'
import { SecretControl } from '@/components/settings/secret-control'
import { PriceBook } from '@/components/settings/price-book'
import { Members } from '@/components/settings/members'
import { WorkspaceGeneral } from '@/components/settings/workspace-general'

export const Route = createFileRoute('/_app/workspaces/$workspaceId/settings')({
  component: SettingsPage,
})

type SectionId = SettingsSection | 'general' | 'prices' | 'members' | 'agents' | 'audit' | 'instance'

const SECTIONS: { id: SectionId; label: string; adminOnly?: boolean }[] = [
  { id: 'general', label: 'General' },
  { id: 'ai', label: 'AI providers' },
  { id: 'github', label: 'GitHub' },
  { id: 'import', label: 'Import limits' },
  { id: 'cost', label: 'Cost' },
  { id: 'prices', label: 'Prices' },
  { id: 'simulation', label: 'Simulation' },
  { id: 'privacy', label: 'Privacy' },
  { id: 'usage', label: 'Usage limits' },
  { id: 'notifications', label: 'Notifications' },
  { id: 'members', label: 'Members' },
  { id: 'agents', label: 'Agent tokens' },
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

  const [section, setSection] = useState<SectionId>('general')
  // Edits live in `edited` until they are saved; without edits the form shows what is stored.
  const [edited, setEdited] = useState<SettingsOverrides | null>(null)
  const saved = settings.data?.overrides
  const draft = edited ?? saved ?? null
  const setDraft = (change: (current: SettingsOverrides) => SettingsOverrides) =>
    setEdited((current) => change(current ?? saved ?? {}))

  const save = useMutation({
    mutationFn: (overrides: SettingsOverrides) =>
      api<WorkspaceSettings>('PUT', `${base}/settings`, overrides, { ifMatch: settings.data?.version ?? 0 }),
    onSuccess: (data) => {
      queryClient.setQueryData(['workspaces', workspaceId, 'settings'], data)
      queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'audit'] })
      setEdited(null)
    },
    onError: shownInline,
  })

  const dirty = useMemo(() => edited !== null && JSON.stringify(edited) !== JSON.stringify(saved ?? {}), [edited, saved])
  const fieldErrors = save.error instanceof ApiError ? save.error.fields : []
  const changedElsewhere = save.error instanceof ApiError && save.error.status === 409
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
            {data.policyViolations.length > 0 && (
              <div role="status" className="mb-6 rounded-lg border border-[var(--trace-retry)]/60 px-4 py-3 text-sm">
                <p className="font-medium text-foreground">The instance policy refuses {data.policyViolations.length === 1 ? 'one value' : `${data.policyViolations.length} values`} of this workspace.</p>
                <ul className="mt-1.5 space-y-1 text-xs text-muted-foreground">
                  {data.policyViolations.map((violation) => (
                    <li key={violation.field}><span className="font-mono text-foreground">{violation.field}</span> · {violation.message}</li>
                  ))}
                </ul>
                <p className="mt-1.5 text-xs text-muted-foreground">Vellum does not use these values. Change them, or ask the instance administrator.</p>
              </div>
            )}
            {section === 'ai' && (
              <>
                <SectionHeading title="AI providers" description="Vellum works without an AI provider. Add one to get proposals, reviews and explanations. Each task uses the provider you select for it." />
                <ProviderList
                  providers={current('ai').providers}
                  overridden={draft.ai?.providers !== undefined}
                  onChange={(providers) => setDraft((d) => setOverride(d, 'ai', 'providers', providers))}
                  onReset={() => setDraft((d) => setOverride(d, 'ai', 'providers', undefined))}
                  errorFor={errorFor}
                  renderSecret={(provider) => {
                    // A key belongs to a saved provider; a test runs against saved settings.
                    const isSaved = data.effective.ai.providers.some((p) => p.id === provider.id)
                    return isSaved ? (
                      <>
                        <SecretControl workspaceId={workspaceId} name={`ai:${provider.id}:apiKey`} label="API key" status={secretFor(`ai:${provider.id}:apiKey`)} canEdit={canEdit} />
                        {canEdit && <ConnectionTest url={`${base}/ai/providers/${provider.id}/test`} blocked={dirty} kind="provider" />}
                      </>
                    ) : (
                      <p className="text-xs text-muted-foreground">Save your changes. Then you can store the API key and test the connection.</p>
                    )
                  }}
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
                  {canEdit && <GitHubCredentialLine workspaceId={workspaceId} tokenSet={secretFor('github:token')?.isSet ?? false} apiBaseUrl={data.effective.github.apiBaseUrl} />}
                  <SecretControl workspaceId={workspaceId} name="github:token" label="Fine-grained access token (read-only)" status={secretFor('github:token')} canEdit={canEdit} />
                  {canEdit && <ConnectionTest url={`${base}/github/test`} blocked={dirty} kind="github" />}
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
                <TextField label="Currency" hint="A three-letter code, such as USD or GBP. An estimate flags a line that is priced in another currency. Vellum converts no currency." {...bind('cost', 'currency')} />
                <NumberField
                  label="A price is stale after"
                  hint="An estimate flags a price that was entered or fetched longer ago than this."
                  min={1} max={3650} suffix="days"
                  {...bind('cost', 'stalePriceAfterDays')}
                />
                <TextField
                  label="Azure price API"
                  hint="The address of the Azure Retail Prices API, or of a mirror. Vellum sends no credential to it. Empty turns the price search off."
                  placeholder="https://prices.azure.com"
                  {...bind('cost', 'azureRetailPricesBaseUrl')}
                />
              </>
            )}

            {section === 'simulation' && (
              <>
                <SectionHeading title="Simulation" description="What a new stress profile starts with. A profile keeps its own values after that. A run records its seed, so the same seed gives the same result." />
                <NumberField label="Seed" min={0} {...bind('simulation', 'seed')} />
                <NumberField label="Duration" min={1} max={3600} suffix="seconds of model time" {...bind('simulation', 'durationSeconds')} />
                <NumberField label="Warm-up" hint="Flows that start before this time are left out of the statistics." min={0} suffix="seconds, less than the duration" {...bind('simulation', 'warmUpSeconds')} />
                <NumberField label="Arrival rate" min={0} step={0.1} suffix="flows per second" {...bind('simulation', 'arrivalRatePerSecond')} />
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
                <UsageTable workspaceId={workspaceId} providers={data.effective.ai.providers} />
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

            {section === 'general' && <WorkspaceGeneral workspaceId={workspaceId} />}
            {section === 'members' && <Members workspaceId={workspaceId} registrationOpen={instance.data?.allowSelfRegistration} />}
            {section === 'agents' && <AgentTokens workspaceId={workspaceId} />}
            {section === 'prices' && <PriceBook workspaceId={workspaceId} currency={data.effective.cost.currency} />}
            {section === 'audit' && <AuditLog workspaceId={workspaceId} />}
            {section === 'instance' && instance.data && <InstancePanel info={instance.data} />}
          </div>
        </main>
      </div>

      {(dirty || save.isError) && section !== 'general' && section !== 'prices' && section !== 'members' && section !== 'agents' && section !== 'audit' && section !== 'instance' && (
        <div role="region" aria-label="Unsaved changes" className="fixed inset-x-0 bottom-0 z-20 border-t border-border bg-card/95 px-4 py-3 backdrop-blur">
          <div className="mx-auto flex max-w-3xl flex-wrap items-center gap-3">
            <p className={cn('min-w-0 flex-1 text-sm', save.isError ? 'text-destructive' : 'text-foreground')} role={save.isError ? 'alert' : undefined}>
              {save.isError
                ? changedElsewhere
                  ? 'Someone else saved these settings while you edited. Load their version, then make your change again.'
                  : fieldErrors.length > 0
                    ? `${fieldErrors.length} ${fieldErrors.length === 1 ? 'value was' : 'values were'} refused. ${fieldErrors[0].message}`
                    : save.error.message
                : 'You have unsaved changes.'}
            </p>
            {changedElsewhere && (
              <Button variant="secondary" size="sm" onClick={() => { setEdited(null); save.reset(); settings.refetch() }}>Load their version</Button>
            )}
            <Button variant="ghost" size="sm" onClick={() => { setEdited(null); save.reset() }}>Discard</Button>
            <Button size="sm" disabled={save.isPending || !dirty || changedElsewhere} onClick={() => save.mutate(draft)}>
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
  const errorId = useId()
  return (
    <label className="block text-xs font-medium text-muted-foreground">
      {label}
      <Input className="mt-1" value={value} aria-invalid={error ? true : undefined} aria-describedby={error ? errorId : undefined} onChange={(e) => onChange(e.target.value)} {...input} />
      {error && <span id={errorId} role="alert" className="mt-1 block font-normal text-destructive">{error}</span>}
    </label>
  )
}

interface AgentToken {
  id: string
  name: string
  scopes: string[]
  projectId: string | null
  createdAt: string
  expiresAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

const AGENT_SCOPES: { id: string; label: string; hint: string }[] = [
  { id: 'read', label: 'Read', hint: 'Read the model and the promises.' },
  { id: 'propose', label: 'Propose', hint: 'Write promise drafts and record check results. A person approves.' },
  { id: 'export', label: 'Export', hint: 'Generate the SDD and the bundle.' },
  { id: 'scan-pull-request', label: 'Scan pull requests', hint: 'Start a scan of a pull request for CI. No other scan. Add Read to read the report.' },
]

/**
 * A coding agent signs in with a token of its own, never with a person's session. A token
 * cannot approve a promise, accept a proposal or change settings.
 */
function AgentTokens({ workspaceId }: { workspaceId: string }) {
  const queryClient = useQueryClient()
  const url = `/api/workspaces/${workspaceId}/agent-tokens`
  const tokens = useQuery({ queryKey: ['workspaces', workspaceId, 'agent-tokens'], queryFn: () => api<AgentToken[]>('GET', url), retry: false })
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState<string[]>(['read'])
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'agent-tokens'] })
  const create = useMutation({
    mutationFn: () => api<{ token: string; name: string; expiresAt: string }>('POST', url, { name: name.trim(), scopes, projectId: null, expiresAt: null }),
    onSuccess: () => { setName(''); return refresh() },
    onError: shownInline,
  })
  const revoke = useMutation({ mutationFn: (id: string) => api<void>('DELETE', `${url}/${id}`), onSuccess: refresh, onError: shownInline })
  const live = tokens.data?.filter((t) => !t.revokedAt) ?? []

  return (
    <>
      <SectionHeading title="Agent tokens" description="A token lets a coding agent use the MCP endpoint and the API of this workspace. A token cannot approve a promise, accept a proposal or change settings. Only a person can do these." />
      {tokens.isError && <p className="mt-4 text-sm text-muted-foreground">Only a workspace owner can manage agent tokens.</p>}
      {tokens.data && (
        <>
          <form className="mt-4 space-y-3 rounded-lg border border-border p-4" onSubmit={(e) => { e.preventDefault(); create.mutate() }}>
            <LabelledInput label="Name" value={name} onChange={setName} placeholder="Claude Code on my laptop" />
            <fieldset>
              <legend className="text-xs font-medium text-muted-foreground">The token can</legend>
              <div className="mt-1.5 space-y-1.5">
                {AGENT_SCOPES.map((scope) => (
                  <label key={scope.id} className="flex items-start gap-2 text-sm text-foreground">
                    <input
                      type="checkbox" className="mt-1 h-3.5 w-3.5 accent-[var(--primary)]" checked={scopes.includes(scope.id)}
                      onChange={() => setScopes(scopes.includes(scope.id) ? scopes.filter((s) => s !== scope.id) : [...scopes, scope.id])}
                    />
                    <span>{scope.label}<span className="block text-xs text-muted-foreground">{scope.hint}</span></span>
                  </label>
                ))}
              </div>
            </fieldset>
            <Button type="submit" size="sm" disabled={create.isPending || !name.trim() || scopes.length === 0}>Create token</Button>
            {create.isError && <p role="alert" className="text-sm text-destructive">{create.error.message}</p>}
          </form>

          {create.data && (
            <div role="status" className="mt-4 rounded-lg border border-[var(--trace-retry)]/60 p-4">
              <p className="text-sm font-medium text-foreground">Copy the token for {create.data.name} now. Vellum does not show it again.</p>
              <div className="mt-2 flex items-center gap-2">
                <code className="min-w-0 flex-1 overflow-x-auto whitespace-nowrap rounded-md bg-muted px-2 py-1.5 font-mono text-xs text-foreground">{create.data.token}</code>
                <Button variant="secondary" size="sm" onClick={() => navigator.clipboard.writeText(create.data.token)}>Copy</Button>
                <Button variant="ghost" size="sm" onClick={() => create.reset()}>Done</Button>
              </div>
              <p className="mt-2 text-xs text-muted-foreground">The agent sends it as <span className="font-mono">Authorization: Bearer …</span> on each request. It expires on {new Date(create.data.expiresAt).toLocaleDateString()}.</p>
            </div>
          )}

          {live.length === 0 && <p className="mt-4 text-sm text-muted-foreground">No token yet.</p>}
          {live.length > 0 && (
            <div className="mt-4 overflow-x-auto rounded-lg border border-border">
              <table className="w-full text-left text-sm">
                <thead className="border-b border-border text-xs text-muted-foreground">
                  <tr><th className="px-3 py-2 font-medium">Name</th><th className="px-3 py-2 font-medium">Can</th><th className="px-3 py-2 font-medium">Last used</th><th className="px-3 py-2 font-medium">Expires</th><th className="px-3 py-2" /></tr>
                </thead>
                <tbody>
                  {live.map((token) => (
                    <tr key={token.id} className="border-b border-border last:border-b-0">
                      <td className="px-3 py-2">{token.name}</td>
                      <td className="px-3 py-2 font-mono text-xs">{token.scopes.join(', ')}</td>
                      <td className="whitespace-nowrap px-3 py-2 text-muted-foreground">{token.lastUsedAt ? new Date(token.lastUsedAt).toLocaleString() : 'Never'}</td>
                      <td className="whitespace-nowrap px-3 py-2 text-muted-foreground">{new Date(token.expiresAt).toLocaleDateString()}</td>
                      <td className="px-3 py-2 text-right">
                        <Button variant="ghost" size="sm" className="h-7 text-xs" disabled={revoke.isPending} onClick={() => revoke.mutate(token.id)}>Revoke</Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {revoke.isError && <p role="alert" className="mt-2 text-sm text-destructive">{revoke.error.message}</p>}
        </>
      )}
    </>
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
      {audit.data && <AuditTable entries={audit.data} />}
    </>
  )
}

/** The newest entries first. An entry names who changed what, never a value. */
function AuditTable({ entries }: { entries: AuditEntry[] }) {
  return (
    <>
      {entries.length === 0 && <p className="mt-4 text-sm text-muted-foreground">Nothing has changed yet.</p>}
      {entries.length > 0 && (
        <div className="mt-4 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr><th className="px-3 py-2 font-medium">When</th><th className="px-3 py-2 font-medium">Action</th><th className="px-3 py-2 font-medium">Target</th><th className="px-3 py-2 font-medium">By</th></tr>
            </thead>
            <tbody>
              {entries.map((entry, i) => (
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
  const diagnostics = useQuery({ queryKey: ['instance', 'diagnostics'], queryFn: () => api<InstanceDiagnostics>('GET', '/api/instance/diagnostics') })
  const instanceAudit = useQuery({ queryKey: ['instance', 'audit'], queryFn: () => api<AuditEntry[]>('GET', '/api/instance/audit?limit=100') })
  const savePolicy = useMutation({
    // The body is exactly the policy and the defaults; the other fields are read-only.
    mutationFn: (next: InstanceSettings) =>
      api<InstanceSettings>('PUT', '/api/instance/settings', { policy: next.policy, defaults: next.defaults }, { ifMatch: next.version }),
    onSuccess: (data) => {
      queryClient.setQueryData(['instance', 'settings'], data)
      queryClient.invalidateQueries({ queryKey: ['instance'] })
      queryClient.invalidateQueries({ queryKey: ['workspaces'] })
    },
    onError: shownInline,
  })
  const rotate = useMutation({ mutationFn: () => api<RotateKeysResult>('POST', '/api/instance/keys/rotate'), onError: shownInline })
  const policy = settings.data?.policy
  const setPolicy = (patch: Partial<InstanceSettings['policy']>) => {
    if (settings.data) savePolicy.mutate({ ...settings.data, policy: { ...settings.data.policy, ...patch } })
  }

  return (
    <>
      <SectionHeading title="Instance" description={`This Vellum instance runs the ${info.profile === 'hosted' ? 'hosted' : 'self-hosted'} profile, version ${info.version}. These policies apply to every workspace.`} />
      {policy && (
        <>
          <ToggleField label="Let people register themselves" hint="With this off, nobody can create an account, and the sign-in screen shows no link to register. Turn it on while a new person registers." value={policy.allowSelfRegistration} onChange={(v) => setPolicy({ allowSelfRegistration: v })} />
          <ToggleField label="Allow URLs on private networks" hint="Needed for a local model server such as Ollama. Keep it off when people you do not know can change settings." value={policy.allowPrivateNetworkUrls} onChange={(v) => setPolicy({ allowPrivateNetworkUrls: v })} />
          <ToggleField label="Let workspaces send private code to AI" hint="A workspace must also turn this on for itself." value={policy.allowSendingPrivateCodeToAi} onChange={(v) => setPolicy({ allowSendingPrivateCodeToAi: v })} />
          <StressRunBound
            key={policy.maxConcurrentStressRuns}
            value={policy.maxConcurrentStressRuns}
            saving={savePolicy.isPending}
            onSave={(v) => setPolicy({ maxConcurrentStressRuns: v })}
          />
        </>
      )}
      {savePolicy.isError && (
        <p role="alert" className="mt-2 text-sm text-destructive">
          {savePolicy.error instanceof ApiError && savePolicy.error.status === 409
            ? 'The instance settings changed while this page was open. Reload the page, then make your change again.'
            : savePolicy.error.message}
        </p>
      )}
      {settings.data && settings.data.workspacesWithPolicyViolations > 0 && (
        <p role="status" className="mt-3 text-xs text-[var(--trace-retry)]">
          {settings.data.workspacesWithPolicyViolations} {settings.data.workspacesWithPolicyViolations === 1 ? 'workspace has' : 'workspaces have'} values that this policy refuses. Vellum does not use those values.
        </p>
      )}

      <ModelPrices />

      <h3 className="mt-8 text-sm font-medium text-foreground">Encryption key</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        To change the master key, start Vellum with the new key and the old key as the previous key. Then re-encrypt. After that, remove the previous key.
      </p>
      <div className="mt-3 flex flex-wrap items-center gap-3">
        <Button variant="secondary" size="sm" disabled={rotate.isPending} onClick={() => rotate.mutate()}>
          {rotate.isPending ? 'Re-encrypting…' : 'Re-encrypt with the current key'}
        </Button>
        {rotate.isSuccess && (
          <span className={cn('text-xs', rotate.data.remaining > 0 ? 'text-[var(--trace-retry)]' : 'text-muted-foreground')} role="status">
            {rotate.data.rewrapped} changed, {rotate.data.remaining} still on an older key. {rotate.data.message}
          </span>
        )}
        {rotate.isError && <span className="text-xs text-destructive" role="alert">{rotate.error.message}</span>}
      </div>

      <h3 className="mt-8 text-sm font-medium text-foreground">Telemetry</h3>
      {diagnostics.data && (
        <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground" data-testid="telemetry-state">
          {diagnostics.data.telemetry.enabled
            ? <>On. Traces, metrics and logs go to <span className="font-mono text-foreground">{diagnostics.data.telemetry.endpointHost}</span>.</>
            : 'Off. This instance sends no traces, metrics or logs to another machine.'}
          {' '}Set the environment variable <span className="font-mono">OTEL_EXPORTER_OTLP_ENDPOINT</span> and restart Vellum to change this.
        </p>
      )}

      <h3 className="mt-8 text-sm font-medium text-foreground">GitHub login from the environment</h3>
      {diagnostics.data && (
        <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground" data-testid="gh-token-state">
          {diagnostics.data.gitHubEnvironmentToken.state === 'in_use'
            ? 'In use. Workspaces that you own and that read api.github.com use GH_TOKEN when they have no token of their own.'
            : diagnostics.data.gitHubEnvironmentToken.state === 'ignored_hosted'
              ? 'GH_TOKEN is set and ignored: the hosted profile uses only workspace tokens.'
              : 'Not set.'}
          {' '}Start Vellum with <span className="font-mono">just up-gh</span> to use your gh login. A change needs a restart.
        </p>
      )}

      <h3 className="mt-8 text-sm font-medium text-foreground">Instance audit log</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        The newest 100 changes to the instance: its setup, its policy and the master key. A workspace has its own log under Audit log.
      </p>
      {instanceAudit.isError && <p role="alert" className="mt-2 text-sm text-destructive">{instanceAudit.error.message}</p>}
      {instanceAudit.data && <AuditTable entries={instanceAudit.data} />}

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

const STRESS_RUN_BOUND = { min: 1, max: 64 }

/**
 * The number is saved with its own button: every other policy is a switch that saves at
 * once, and a number that saved on each key press would save "1" on the way to "16".
 */
function StressRunBound({ value, saving, onSave }: { value: number; saving: boolean; onSave: (value: number) => void }) {
  const [draft, setDraft] = useState<number | null>(value)
  const valid = draft !== null && Number.isInteger(draft) && draft >= STRESS_RUN_BOUND.min && draft <= STRESS_RUN_BOUND.max
  return (
    <div className="flex items-start gap-2">
      <div className="min-w-0 flex-1">
        <NumberField<number | null>
          label="Stress runs at the same time"
          hint="For all workspaces together, in each app process. One run holds a processor thread until it ends. A change applies to the next run."
          min={STRESS_RUN_BOUND.min} max={STRESS_RUN_BOUND.max} step={1} nullable
          value={draft}
          onChange={setDraft}
          error={valid ? undefined : `Enter a whole number from ${STRESS_RUN_BOUND.min} to ${STRESS_RUN_BOUND.max}.`}
        />
      </div>
      <Button variant="secondary" size="sm" className="mt-4" disabled={!valid || draft === value || saving} onClick={() => valid && onSave(draft)}>
        Save
      </Button>
    </div>
  )
}

/** A real call through the same path a task uses. The result says what to fix. */
function ConnectionTest({ url, blocked, kind }: { url: string; blocked: boolean; kind: 'provider' | 'github' }) {
  const test = useMutation({
    mutationFn: () => api<ProviderTestResult | GitHubTestResult>('POST', url),
    onError: shownInline,
  })
  const result = test.data
  return (
    <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5">
      <Button variant="secondary" size="sm" disabled={blocked || test.isPending} onClick={() => test.mutate()}>
        {test.isPending ? 'Testing…' : 'Test connection'}
      </Button>
      {blocked && <span className="text-xs text-muted-foreground">Save your changes first. The test uses the saved settings.</span>}
      {!blocked && result && (
        <p role="status" className={cn('flex min-w-0 items-start gap-1.5 text-xs', result.ok ? 'text-[var(--trace-request)]' : 'text-[var(--trace-failure)]')}>
          {result.ok ? <CircleCheck className="mt-px h-3.5 w-3.5 shrink-0" /> : <CircleX className="mt-px h-3.5 w-3.5 shrink-0" />}
          <span>
            {result.ok
              ? kind === 'provider'
                ? describeProviderTest(result as ProviderTestResult)
                : describeGitHubTest(result as GitHubTestResult)
              : result.error?.message ?? 'The connection did not work.'}
          </span>
        </p>
      )}
      {!blocked && test.isError && <p role="alert" className="text-xs text-destructive">{test.error.message}</p>}
    </div>
  )
}

function describeProviderTest(result: ProviderTestResult): string {
  const model = result.reportedModel && result.reportedModel !== result.model ? `${result.model} (the provider reports ${result.reportedModel})` : result.model
  const structured = result.supportsStructuredOutput === false ? ' It does not return structured output; turn that capability off.' : ''
  return `${model} answered in ${result.latencyMs} ms.${structured}`
}

/** Which token the reads use: the workspace's own, the gh login of a local instance, or none. */
function GitHubCredentialLine({ workspaceId, tokenSet, apiBaseUrl }: { workspaceId: string; tokenSet: boolean; apiBaseUrl: string }) {
  // The server answers from the saved token and the saved API base URL, so both are in the key.
  const credential = useQuery({
    queryKey: ['workspaces', workspaceId, 'github-credential', tokenSet, apiBaseUrl],
    queryFn: () => api<GitHubCredential>('GET', `/api/workspaces/${workspaceId}/github/credential`),
  })
  const c = credential.data
  if (!c) return null
  const text =
    c.source === 'workspace' ? 'GitHub: workspace token.'
    : c.source === 'gh' ? `GitHub: your gh login${c.login ? ` (${c.login})` : ''}, from GH_TOKEN.`
    : 'GitHub: anonymous. Public repositories only.'
  const note =
    c.ghNotUsed === 'not_admin_owned' ? 'The gh login of this instance is for workspaces that the instance admin owns.'
    : c.ghNotUsed === 'other_host' ? 'The gh login of this instance is for api.github.com only.'
    : null
  return (
    <div role="status" className="mb-3 text-xs">
      <p className="text-foreground">{text}</p>
      {note && <p className="mt-0.5 text-muted-foreground">{note}</p>}
    </div>
  )
}

function describeGitHubTest(result: GitHubTestResult): string {
  const who = result.authenticatedAs ? `Signed in as ${result.authenticatedAs}.` : 'Connected without a token: public repositories only.'
  return result.rateLimitRemaining === null ? who : `${who} ${result.rateLimitRemaining} requests remain in this hour.`
}

function UsageTable({ workspaceId, providers }: { workspaceId: string; providers: AiProvider[] }) {
  const usage = useQuery({
    queryKey: ['workspaces', workspaceId, 'ai-usage'],
    queryFn: () => api<AiUsage>('GET', `/api/workspaces/${workspaceId}/ai/usage`),
  })
  const nameOf = (id: string) => providers.find((p) => p.id === id)?.name ?? 'Removed provider'
  const tokens = (count: number | null) => (count === null ? 'not reported' : count.toLocaleString())

  return (
    <section className="mt-8" aria-labelledby="usage-heading">
      <h3 id="usage-heading" className="text-sm font-medium text-foreground">AI use this month</h3>
      {usage.data?.totals.length === 0 && <p className="mt-2 text-sm text-muted-foreground">No AI call was made this month.</p>}
      {usage.isError && <p className="mt-2 text-sm text-destructive">{usage.error.message}</p>}
      {usage.data && usage.data.totals.length > 0 && (
        <div className="mt-2 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">Provider</th><th className="px-3 py-2 font-medium">Model</th><th className="px-3 py-2 font-medium">Task</th>
                <th className="px-3 py-2 text-right font-medium">Calls</th><th className="px-3 py-2 text-right font-medium">Tokens in</th>
                <th className="px-3 py-2 text-right font-medium">Tokens out</th><th className="px-3 py-2 text-right font-medium">Estimated cost</th>
              </tr>
            </thead>
            <tbody>
              {usage.data.totals.map((row) => (
                <tr key={`${row.providerId}-${row.model}-${row.task}`} className="border-b border-border last:border-b-0">
                  <td className="px-3 py-2">{nameOf(row.providerId)}</td>
                  <td className="px-3 py-2 font-mono text-xs">{row.model}</td>
                  <td className="px-3 py-2">{row.task}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{row.calls}</td>
                  <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">{tokens(row.inputTokens)}</td>
                  <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">{tokens(row.outputTokens)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    {row.costUsd === null
                      ? 'unknown'
                      : `${row.costKnown ? '' : 'at least '}${row.costEstimated ? 'up to ' : ''}$${row.costUsd.toFixed(4)}`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="mt-2 text-xs text-muted-foreground">
        A cost is an estimate from the price table of this instance. It is unknown when the model has no price. “Up to” means the provider reported no token count, so Vellum counts the most the call could cost.
      </p>
    </section>
  )
}

function ModelPrices() {
  const queryClient = useQueryClient()
  const prices = useQuery({ queryKey: ['instance', 'ai-prices'], queryFn: () => api<ModelPrice[]>('GET', '/api/instance/ai/prices') })
  const [form, setForm] = useState({ providerKind: 'openai' as ProviderKind, model: '', input: '', output: '' })
  const add = useMutation({
    mutationFn: () => api<ModelPrice>('POST', '/api/instance/ai/prices', {
      providerKind: form.providerKind,
      model: form.model.trim(),
      inputUsdPerMillionTokens: Number(form.input),
      outputUsdPerMillionTokens: Number(form.output),
      effectiveFrom: null,
    }),
    onSuccess: () => {
      setForm((current) => ({ ...current, model: '', input: '', output: '' }))
      queryClient.invalidateQueries({ queryKey: ['instance', 'ai-prices'] })
    },
    onError: shownInline,
  })
  const fieldError = add.error instanceof ApiError ? add.error.fields[0]?.message : undefined

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Model prices</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        Vellum ships no price. Enter the price your provider charges, in USD for one million tokens. Without a price a cost shows as unknown, and a call under a budget is refused.
      </p>
      {prices.data && prices.data.length > 0 && (
        <div className="mt-3 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">Provider</th><th className="px-3 py-2 font-medium">Model</th>
                <th className="px-3 py-2 text-right font-medium">Input</th><th className="px-3 py-2 text-right font-medium">Output</th>
                <th className="px-3 py-2 font-medium">In effect from</th>
              </tr>
            </thead>
            <tbody>
              {prices.data.map((price) => (
                <tr key={`${price.providerKind}-${price.model}-${price.effectiveFrom}`} className="border-b border-border last:border-b-0">
                  <td className="px-3 py-2">{PROVIDER_PRESETS[price.providerKind]?.label.split(' (')[0] ?? price.providerKind}</td>
                  <td className="px-3 py-2 font-mono text-xs">{price.model}</td>
                  <td className="px-3 py-2 text-right tabular-nums">${price.inputUsdPerMillionTokens}</td>
                  <td className="px-3 py-2 text-right tabular-nums">${price.outputUsdPerMillionTokens}</td>
                  <td className="px-3 py-2 text-muted-foreground">{new Date(price.effectiveFrom).toLocaleDateString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <form className="mt-3 grid items-end gap-2 sm:grid-cols-[10rem_minmax(0,1fr)_7rem_7rem_auto]" onSubmit={(e) => { e.preventDefault(); add.mutate() }}>
        <label className="block text-xs font-medium text-muted-foreground">
          Provider
          <select className={cn(selectClass, 'mt-1')} value={form.providerKind} onChange={(e) => setForm({ ...form, providerKind: e.target.value as ProviderKind })}>
            {(Object.keys(PROVIDER_PRESETS) as ProviderKind[]).map((kind) => <option key={kind} value={kind}>{PROVIDER_PRESETS[kind].label.split(' (')[0]}</option>)}
          </select>
        </label>
        <LabelledInput label="Model" value={form.model} onChange={(model) => setForm({ ...form, model })} />
        <LabelledInput label="Input (USD)" type="number" value={form.input} onChange={(input) => setForm({ ...form, input })} />
        <LabelledInput label="Output (USD)" type="number" value={form.output} onChange={(output) => setForm({ ...form, output })} />
        <Button type="submit" size="sm" variant="secondary" disabled={add.isPending || !form.model.trim() || form.input === '' || form.output === ''}>Add price</Button>
      </form>
      {add.isError && <p role="alert" className="mt-2 text-xs text-destructive">{fieldError ?? add.error.message}</p>}
    </>
  )
}
