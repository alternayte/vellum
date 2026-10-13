import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import {
  RESULT_COLOR, RESULT_LABEL, emptyPromise, promisesApi,
  type EvidenceSource, type Obligation, type ObligationStatus, type PromiseDetail, type PromiseInput, type PromiseLinks, type PromiseRevision,
} from '@/lib/promises-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'

interface Props {
  projectId: string
  promiseId: string | null
  references: ScenarioReference[]
  scenarios: { id: string; name: string }[]
  onClose: () => void
}

const areaClass =
  'mt-1 min-h-16 w-full resize-y rounded-lg border border-input bg-transparent p-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:opacity-70'

const LINK_GROUPS: { field: keyof PromiseLinks; label: string; group: ScenarioReference['group'] | 'Scenarios' }[] = [
  { field: 'scenarioIds', label: 'Scenarios', group: 'Scenarios' },
  { field: 'operationIds', label: 'Operations', group: 'Operations' },
  { field: 'channelIds', label: 'Channels', group: 'Channels' },
  { field: 'subscriptionIds', label: 'Subscriptions', group: 'Subscriptions' },
  { field: 'messageIds', label: 'Messages', group: 'Messages' },
  { field: 'elementIds', label: 'Elements', group: 'Elements' },
]

const FIELD_LABEL: Record<string, string> = {
  title: 'Title', claim: 'Claim', scope: 'Scope', preconditions: 'Preconditions', expectedOutcome: 'Expected outcome',
  forbiddenOutcome: 'Forbidden outcome', evidenceCriteria: 'What counts as evidence',
}

/** The error of a request, with each refused field named. */
function Failure({ error }: { error: Error }) {
  return (
    <div role="alert" className="text-xs text-destructive">
      <p>{error.message}</p>
      {error instanceof ApiError && error.fields.length > 0 && (
        <ul className="mt-0.5 list-disc pl-4">
          {error.fields.map((e, i) => <li key={i}>{FIELD_LABEL[e.field] ?? e.field}: {e.message}</li>)}
        </ul>
      )}
    </div>
  )
}

function toInput(revision: PromiseRevision): PromiseInput {
  const { title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links } = revision
  return { title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links }
}

/**
 * One promise: the claim, what it is linked to, and the obligations that would count as
 * evidence. An approved revision cannot be edited; a change is a new revision that needs
 * approval again.
 */
export function PromiseDialog({ projectId, promiseId, references, scenarios, onClose }: Props) {
  const queryClient = useQueryClient()
  const [id, setId] = useState(promiseId)
  const loaded = useQuery({
    queryKey: ['projects', projectId, 'promises', id],
    queryFn: () => promisesApi.get(projectId, id!),
    enabled: id !== null,
  })
  const promise = loaded.data ?? null
  // The draft is what a person edits; without one, the dialog shows the revision in force.
  const revision = promise ? (promise.draft ?? promise.current) : null
  const editable = promise === null ? id === null : revision?.status === 'draft'

  const [edited, setEdited] = useState<PromiseInput | null>(null)
  const form = edited ?? (revision ? toInput(revision) : emptyPromise())
  const set = <K extends keyof PromiseInput>(field: K, value: PromiseInput[K]) => setEdited({ ...form, [field]: value })
  const text = (field: 'scope' | 'preconditions' | 'expectedOutcome' | 'forbiddenOutcome' | 'evidenceCriteria') => ({
    value: form[field] ?? '',
    onChange: (e: React.ChangeEvent<HTMLTextAreaElement>) => set(field, e.target.value || null),
    disabled: !editable,
  })

  const store = (next: PromiseDetail) => {
    setId(next.id)
    setEdited(null)
    queryClient.setQueryData(['projects', projectId, 'promises', next.id], next)
  }
  const save = useMutation({
    mutationFn: () => (id ? promisesApi.update(projectId, id, form) : promisesApi.create(projectId, form)),
    onSuccess: store, onError: shownInline,
  })
  const generate = useMutation({ mutationFn: () => promisesApi.generate(projectId, id!), onSuccess: (r) => store(r.promise), onError: shownInline })
  const reload = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises', id] })
  const approve = useMutation({
    mutationFn: () => promisesApi.approve(projectId, id!, revision!.revision, revision!.contentHash),
    onSuccess: store,
    // The content changed after it was loaded: show what is stored now, so the approver reads it first.
    onError: (error) => { shownInline(); if (/changed/i.test(error.message)) void reload() },
  })
  const revise = useMutation({ mutationFn: () => promisesApi.revise(projectId, id!), onSuccess: store, onError: shownInline })
  const run = useMutation({
    // A draft is run by its number. That run is labelled as a draft run and does not count after approval.
    mutationFn: (key: string) => promisesApi.run(projectId, id!, key, null, revision!.status === 'draft' ? revision!.revision : null),
    onSuccess: reload,
    onError: shownInline,
  })
  const failure = save.error ?? generate.error ?? approve.error ?? revise.error ?? run.error

  const toggleLink = (field: keyof PromiseLinks, linkId: string) => {
    const current = form.links[field]
    set('links', { ...form.links, [field]: current.includes(linkId) ? current.filter((x) => x !== linkId) : [...current, linkId] })
  }
  const options = (group: (typeof LINK_GROUPS)[number]['group']) =>
    group === 'Scenarios' ? scenarios.map((s) => ({ id: s.id, label: s.name })) : references.filter((r) => r.group === group)
  const statusOf = (obligationId: string): ObligationStatus | undefined => revision?.evidenceStatus.obligations.find((o) => o.obligationId === obligationId)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>
            {promise ? <><span className="font-mono">{promise.requirementId}</span> · {revision?.title}</> : 'New promise'}
          </DialogTitle>
          <DialogDescription>
            {promise === null
              ? 'State what must always hold. You approve it before it counts as a requirement.'
              : revision?.status === 'draft'
                ? `Revision ${revision.revision} is a draft.${promise.inForceRevision ? ` Revision ${promise.inForceRevision} stays in force until this one is approved.` : ' It is not a requirement until an owner approves it.'}`
                : `Revision ${revision?.revision} is approved and cannot be edited. To change it, start a new revision.`}
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
          <div className="grid gap-3 md:grid-cols-2">
            <label className="block text-xs font-medium text-muted-foreground md:col-span-2">
              Title
              <Input className="mt-1" value={form.title} onChange={(e) => set('title', e.target.value)} disabled={!editable} placeholder="An accepted order survives a broker outage" />
            </label>
            <label className="block text-xs font-medium text-muted-foreground md:col-span-2">
              Claim
              <textarea className={areaClass} value={form.claim} onChange={(e) => set('claim', e.target.value)} disabled={!editable} placeholder="When Orders accepts an order, the OrderCreated event reaches its consumers, also when the broker is down at that moment." />
            </label>
            <label className="block text-xs font-medium text-muted-foreground">Scope<textarea className={areaClass} {...text('scope')} /></label>
            <label className="block text-xs font-medium text-muted-foreground">Preconditions<textarea className={areaClass} {...text('preconditions')} /></label>
            <label className="block text-xs font-medium text-muted-foreground">Expected outcome<textarea className={areaClass} {...text('expectedOutcome')} /></label>
            <label className="block text-xs font-medium text-muted-foreground">Forbidden outcome<textarea className={areaClass} {...text('forbiddenOutcome')} /></label>
            <label className="block text-xs font-medium text-muted-foreground md:col-span-2">What counts as evidence<textarea className={areaClass} {...text('evidenceCriteria')} /></label>
          </div>

          <section aria-labelledby="promise-links">
            <h3 id="promise-links" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Linked to</h3>
            {promise?.missingLinks.length ? (
              <p role="status" className="mt-1 text-xs text-[var(--trace-retry)]">
                {promise.missingLinks.length} linked {promise.missingLinks.length === 1 ? 'part no longer exists' : 'parts no longer exist'} in the model. Review this promise.
              </p>
            ) : null}
            <div className="mt-1.5 grid gap-x-4 gap-y-2 sm:grid-cols-2 md:grid-cols-3">
              {LINK_GROUPS.map(({ field, label, group }) => {
                const items = options(group)
                if (items.length === 0) return null
                return (
                  <fieldset key={field} className="min-w-0">
                    <legend className="text-[11px] text-muted-foreground">{label}</legend>
                    <ul className="mt-0.5 max-h-28 space-y-0.5 overflow-y-auto">
                      {items.map((item) => (
                        <li key={item.id}>
                          <label className="flex items-center gap-1.5 text-xs text-foreground">
                            <input type="checkbox" className="h-3 w-3 accent-[var(--primary)]" checked={form.links[field].includes(item.id)} disabled={!editable} onChange={() => toggleLink(field, item.id)} />
                            <span className="truncate">{item.label}</span>
                          </label>
                        </li>
                      ))}
                    </ul>
                  </fieldset>
                )
              })}
            </div>
          </section>

          {promise && revision && (
            <section aria-labelledby="promise-obligations">
              <div className="flex items-center justify-between">
                <h3 id="promise-obligations" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Obligations</h3>
                {editable && (
                  <Button variant="ghost" size="sm" className="h-6 text-xs" disabled={generate.isPending || edited !== null} onClick={() => generate.mutate()} title={edited ? 'Save your changes first' : undefined}>
                    Generate from the links
                  </Button>
                )}
              </div>
              {revision.obligations.length === 0 && (
                <p className="mt-1 text-xs text-muted-foreground">No obligation yet. A promise without obligations has no evidence and never passes.</p>
              )}
              <ul className="mt-1.5 space-y-1.5">
                {revision.obligations.map((obligation) => {
                  const status = statusOf(obligation.id)
                  const result = status?.result ?? (obligation.supported ? 'not-run' : 'unsupported')
                  const stale = status?.current === false
                  const color = stale ? 'var(--trace-retry)' : RESULT_COLOR[result]
                  return (
                    <li key={obligation.id} className="rounded-md border border-border px-3 py-2">
                      <div className="flex flex-wrap items-baseline gap-x-2">
                        <span className="font-mono text-[11px] text-muted-foreground">{obligation.id}</span>
                        <span className="min-w-0 flex-1 text-sm text-foreground">{obligation.title}</span>
                        <span className="rounded-full border px-1.5 text-[11px]" style={{ borderColor: color, color }}>{RESULT_LABEL[result]}{stale ? ' (stale)' : ''}</span>
                        {obligation.execution === 'deterministic' && (
                          <Button variant="secondary" size="sm" className="h-6 text-xs" disabled={run.isPending} onClick={() => run.mutate(obligation.key)}>Run</Button>
                        )}
                      </div>
                      <p className="mt-0.5 font-mono text-[10px] text-muted-foreground">
                        {obligation.kind}{obligation.execution === 'external-evidence' ? ' · a test plan: Vellum cannot run it; it needs a result from your CI' : obligation.execution === 'none' ? ' · Vellum cannot check this kind' : ''}
                      </p>
                      {obligation.recipe.plan && (
                        <PlanEvidence
                          key={obligation.id + (obligation.recipe.plan.evidenceSource ? JSON.stringify(obligation.recipe.plan.evidenceSource) : '')}
                          projectId={projectId} promiseId={promise.id} obligation={obligation} editable={editable && edited === null}
                          revision={revision.status === 'draft' ? revision.revision : null} onStored={store} onRecorded={reload}
                        />
                      )}
                      {status && status.result !== 'not-run' && (
                        <div className="mt-1.5 space-y-0.5 text-xs text-muted-foreground">
                          {stale && status.changed.length > 0 && (
                            <div className="text-[var(--trace-retry)]">
                              <p>This result is stale. It was made before these changes:</p>
                              <ul className="list-disc pl-4">{status.changed.map((change, i) => <li key={i}>{change}</li>)}</ul>
                            </div>
                          )}
                          {status.scope && <p><span className="text-foreground">Checked:</span> {status.scope}</p>}
                          {status.environment && <p><span className="text-foreground">Against:</span> <span className="font-mono">{status.environment}</span>{status.at ? ` on ${new Date(status.at).toLocaleString()}` : ''}</p>}
                          {status.detail && <p>{status.detail}</p>}
                          {status.evidence.map((e, i) => (
                            <p key={i} className="font-mono text-[11px]">
                              {e.kind === 'trace' ? `trace ${e.hash?.slice(0, 12)}` : e.kind === 'source' ? e.pointer : `external · ${e.identity}`}
                              {e.url && <> · <a className="underline-offset-2 hover:underline" href={e.url} target="_blank" rel="noreferrer noopener">open</a></>}
                            </p>
                          ))}
                          {status.assertions?.filter((a) => a.vacuous).map((a) => (
                            <p key={a.id}><span className="text-foreground">Checks nothing:</span> <span className="font-mono">{a.id}</span> · {a.vacuousReason}</p>
                          ))}
                          {status.limitations.map((limitation, i) => <p key={i}><span className="text-foreground">Limit:</span> {limitation}</p>)}
                        </div>
                      )}
                    </li>
                  )
                })}
              </ul>
              {revision.evidenceStatus.gaps.length > 0 && (
                <div className="mt-2 rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs">
                  <p className="font-medium text-foreground">Evidence gaps</p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-muted-foreground">{revision.evidenceStatus.gaps.map((gap, i) => <li key={i}>{gap}</li>)}</ul>
                </div>
              )}
              <p className="mt-2 text-[11px] text-muted-foreground">A passed check is evidence for what it checked, in the scope shown. It does not prove the promise in general.</p>
            </section>
          )}
        </div>

        {failure && <Failure error={failure} />}

        <div className="flex flex-wrap items-center gap-2">
          <div className="flex-1" />
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          {editable && (
            <Button variant="secondary" size="sm" disabled={save.isPending || (id !== null && edited === null) || !form.title.trim() || !form.claim.trim()} onClick={() => save.mutate()}>
              {id ? 'Save draft' : 'Create draft'}
            </Button>
          )}
          {promise && revision?.status === 'draft' && (
            <Button size="sm" className={cn(edited && 'opacity-60')} disabled={approve.isPending || edited !== null} title={edited ? 'Save your changes first' : 'Only a workspace owner can approve'} onClick={() => approve.mutate()}>
              Approve revision {revision.revision}
            </Button>
          )}
          {promise && revision?.status === 'approved' && (
            <Button variant="secondary" size="sm" disabled={revise.isPending} onClick={() => revise.mutate()}>Start a new revision</Button>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

/**
 * A test plan is run outside Vellum. Its result counts only when it comes from the workflow
 * that the obligation names: Vellum reads the run from GitHub and compares the repository,
 * the workflow, the job and the commit.
 */
function PlanEvidence({ projectId, promiseId, obligation, editable, revision, onStored, onRecorded }: {
  projectId: string
  promiseId: string
  obligation: Obligation
  editable: boolean
  revision: number | null
  onStored: (promise: PromiseDetail) => void
  onRecorded: () => void
}) {
  const plan = obligation.recipe.plan!
  const [source, setSource] = useState<EvidenceSource>(plan.evidenceSource ?? { system: 'github-actions', repository: '', workflow: '', job: null })
  const [runUrl, setRunUrl] = useState('')
  const [commitSha, setCommitSha] = useState('')
  const saveSource = useMutation({
    mutationFn: () => promisesApi.updateObligation(projectId, promiseId, obligation, { ...obligation.recipe, plan: { ...plan, evidenceSource: { ...source, job: source.job?.trim() || null } } }),
    onSuccess: onStored, onError: shownInline,
  })
  const record = useMutation({
    mutationFn: () => promisesApi.recordEvidence(projectId, promiseId, obligation.key, runUrl.trim(), commitSha.trim() || null, revision),
    onSuccess: () => { setRunUrl(''); setCommitSha(''); onRecorded() }, onError: shownInline,
  })
  const failure = saveSource.error ?? record.error
  const field = 'h-7 text-xs'

  return (
    <div className="mt-1.5 space-y-1.5 rounded-md bg-muted/40 p-2 text-xs">
      {plan.evidenceSource === null && !editable && (
        <p className="text-[var(--trace-retry)]">This plan names no evidence source. It cannot pass. Start a new revision to name the workflow that runs it.</p>
      )}
      {editable ? (
        <div className="grid gap-1.5 sm:grid-cols-[1fr_1fr_1fr_auto]">
          <Input className={field} aria-label="Repository" placeholder="owner/name" value={source.repository} onChange={(e) => setSource({ ...source, repository: e.target.value })} spellCheck={false} />
          <Input className={field} aria-label="Workflow" placeholder="Workflow file or name" value={source.workflow} onChange={(e) => setSource({ ...source, workflow: e.target.value })} spellCheck={false} />
          <Input className={field} aria-label="Job (optional)" placeholder="Job (optional)" value={source.job ?? ''} onChange={(e) => setSource({ ...source, job: e.target.value })} spellCheck={false} />
          <Button variant="secondary" size="sm" className="h-7 text-xs" disabled={saveSource.isPending || !source.repository.trim() || !source.workflow.trim()} onClick={() => saveSource.mutate()}>
            Set source
          </Button>
          <p className="text-[11px] text-muted-foreground sm:col-span-4">The result of this plan counts only when it comes from this GitHub Actions workflow, in a repository that this project imports.</p>
        </div>
      ) : plan.evidenceSource && (
        <>
          <p className="text-muted-foreground">
            Evidence source: <span className="font-mono text-foreground">{plan.evidenceSource.repository}</span> · workflow <span className="font-mono text-foreground">{plan.evidenceSource.workflow}</span>
            {plan.evidenceSource.job && <> · job <span className="font-mono text-foreground">{plan.evidenceSource.job}</span></>}
          </p>
          <div className="grid gap-1.5 sm:grid-cols-[2fr_1fr_auto]">
            <Input className={field} aria-label="Workflow run URL" placeholder="URL of the workflow run" value={runUrl} onChange={(e) => setRunUrl(e.target.value)} spellCheck={false} />
            <Input className={field} aria-label="Commit (optional)" placeholder="Commit (optional)" value={commitSha} onChange={(e) => setCommitSha(e.target.value)} spellCheck={false} />
            <Button variant="secondary" size="sm" className="h-7 text-xs" disabled={record.isPending || !runUrl.trim()} onClick={() => record.mutate()}>
              {record.isPending ? 'Reading the run…' : 'Record run'}
            </Button>
          </div>
        </>
      )}
      {failure && <Failure error={failure} />}
    </div>
  )
}
