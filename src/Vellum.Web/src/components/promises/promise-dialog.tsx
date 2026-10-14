import { useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Crosshair, Pencil, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { importApi } from '@/lib/import-api'
import { branchesApi } from '@/lib/branches-api'
import { useProject } from '@/hooks/use-model'
import { promiseElementIds } from '@/lib/scenario-parts'
import { readableSegments } from '@/lib/readable-ids'
import { useShellStore } from '@/stores/shell-store'
import {
  describeResult, emptyPromise, isAgentActor, promisesApi, shortAgent,
  type EvidenceSource, type Obligation, type ObligationStatus, type PromiseDetail, type PromiseInput, type PromiseLinks, type PromiseRevision,
} from '@/lib/promises-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { ObligationDialog } from './obligation-dialog'
import { RunHistory } from './run-history'
import { TargetThreads } from '@/components/comments/target-threads'
import { Failure, Field, ForbiddenEvents, PatternFields, areaClass, smallSelect } from './promise-fields'

interface Props {
  projectId: string
  promiseId: string | null
  references: ScenarioReference[]
  scenarios: { id: string; name: string }[]
  /** The element that owns an operation, a subscription or a message: what the canvas shows for it. */
  owners: Record<string, string>
  onClose: () => void
}

const LINK_GROUPS: { field: keyof PromiseLinks; label: string; group: ScenarioReference['group'] | 'Scenarios' }[] = [
  { field: 'scenarioIds', label: 'Scenarios', group: 'Scenarios' },
  { field: 'operationIds', label: 'Operations', group: 'Operations' },
  { field: 'channelIds', label: 'Channels', group: 'Channels' },
  { field: 'subscriptionIds', label: 'Subscriptions', group: 'Subscriptions' },
  { field: 'messageIds', label: 'Messages', group: 'Messages' },
  { field: 'elementIds', label: 'Elements', group: 'Elements' },
]

type TextField = 'scope' | 'preconditions' | 'expectedOutcome' | 'forbiddenOutcome' | 'evidenceCriteria'

/** The fields an approval needs beside the title and the claim, with the name each has on the screen. */
const NEEDED: { field: TextField; label: string }[] = [
  { field: 'preconditions', label: 'Preconditions' },
  { field: 'expectedOutcome', label: 'Expected outcome' },
  { field: 'forbiddenOutcome', label: 'Forbidden outcome' },
  { field: 'evidenceCriteria', label: 'What counts as evidence' },
]

function toInput(revision: PromiseRevision): PromiseInput {
  const { title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links } = revision
  return { title, claim, scope, preconditions, expectedOutcome, forbiddenOutcome, evidenceCriteria, forbiddenEvents, requiresPattern, links }
}

/**
 * One promise: the claim, what it is linked to, and the obligations that would count as
 * evidence. An approved revision cannot be edited; a change is a new revision that needs
 * approval again.
 */
export function PromiseDialog({ projectId, promiseId, references, scenarios, owners, onClose }: Props) {
  const queryClient = useQueryClient()
  const [id, setId] = useState(promiseId)
  const loaded = useQuery({
    queryKey: ['projects', projectId, 'promises', id],
    queryFn: () => promisesApi.get(projectId, id!),
    enabled: id !== null,
  })
  // A check of a source pattern reads one completed scan, so the dialog offers the scans there are.
  const scans = useQuery({ queryKey: ['projects', projectId, 'imports'], queryFn: () => importApi.list(projectId) })
  // A check names the model stream it played on; these name the streams of this project.
  const project = useProject(projectId)
  const drafts = useQuery({ queryKey: ['projects', projectId, 'drafts', 'open'], queryFn: () => branchesApi.drafts(projectId) })
  const variants = useQuery({ queryKey: ['projects', projectId, 'variants'], queryFn: () => branchesApi.variants(projectId) })
  const completed = (scans.data ?? []).filter((s) => s.status === 'completed')
  const repositories = [...new Set(completed.map((s) => s.repository.toLowerCase()))]

  const promise = loaded.data ?? null
  // The draft is what a person edits; without one, the dialog shows the revision in force.
  const revision = promise ? (promise.draft ?? promise.current) : null
  const editable = promise === null ? id === null : revision?.status === 'draft' && !revision.retires

  const [edited, setEdited] = useState<PromiseInput | null>(null)
  const form = edited ?? (revision ? toInput(revision) : emptyPromise())
  const set = <K extends keyof PromiseInput>(field: K, value: PromiseInput[K]) => setEdited({ ...form, [field]: value })
  const [editing, setEditing] = useState<Obligation | 'new' | null>(null)
  const [scanFor, setScanFor] = useState<Record<string, string>>({})

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
  const revise = useMutation({ mutationFn: (retire: boolean) => promisesApi.revise(projectId, id!, retire), onSuccess: store, onError: shownInline })
  const removeObligation = useMutation({ mutationFn: (key: string) => promisesApi.removeObligation(projectId, id!, key), onSuccess: store, onError: shownInline })
  const run = useMutation({
    // A draft is run by its number. That run is labelled as a draft run and does not count after approval.
    mutationFn: ({ key, scanId }: { key: string; scanId: string | null }) =>
      promisesApi.run(projectId, id!, key, scanId, revision!.status === 'draft' ? revision!.revision : null),
    onSuccess: reload,
    onError: shownInline,
  })
  const show = useMutation({
    mutationFn: () => promiseElementIds(projectId, revision!, owners),
    onSuccess: (elementIds) => {
      useShellStore.getState().showOnCanvas({ title: `${promise!.requirementId} · ${revision!.title}`, elementIds })
      onClose()
    },
    onError: shownInline,
  })
  // Only a promise that was never approved is deleted. An approved one is retired by a revision.
  const remove = useMutation({
    mutationFn: () => promisesApi.remove(projectId, id!),
    onSuccess: async () => {
      queryClient.removeQueries({ queryKey: ['projects', projectId, 'promises', id] })
      await queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises'] })
      onClose()
    },
    onError: shownInline,
  })
  // The save error stays next to its fields. The other errors have no field on this screen.
  const failure = generate.error ?? approve.error ?? revise.error ?? removeObligation.error ?? run.error ?? show.error ?? remove.error

  const toggleLink = (field: keyof PromiseLinks, linkId: string) => {
    const current = form.links[field]
    set('links', { ...form.links, [field]: current.includes(linkId) ? current.filter((x) => x !== linkId) : [...current, linkId] })
  }
  const options = (group: (typeof LINK_GROUPS)[number]['group']) =>
    group === 'Scenarios' ? scenarios.map((s) => ({ id: s.id, label: s.name })) : references.filter((r) => r.group === group)
  const statusOf = (obligationId: string): ObligationStatus | undefined => revision?.evidenceStatus.obligations.find((o) => o.obligationId === obligationId)
  const nameOf = (partId: string) => references.find((r) => r.id === partId)?.label ?? scenarios.find((s) => s.id === partId)?.name ?? `id:${partId.slice(0, 8)} (not in the model)`
  // The name of an ID in a server text: a part of the model, a scenario, or a scan of this project.
  const knownName = (partId: string): string | undefined => {
    const part = references.find((r) => r.id === partId)?.label ?? scenarios.find((s) => s.id === partId)?.name
    if (part) return part
    if (partId === project.data?.streamId) return 'main'
    const branch = drafts.data?.find((d) => d.streamId === partId) ?? variants.data?.find((v) => v.streamId === partId)
    if (branch) return branch.name
    const scan = (scans.data ?? []).find((s) => s.id === partId)
    return scan ? `${scan.ref ?? scan.trackedRef ?? 'default branch'} at ${scan.commitSha?.slice(0, 10) ?? 'an unknown commit'}` : undefined
  }
  const readable = (text: string) => <Readable text={text} nameOf={knownName} />

  const area = (field: TextField, label: string, wide = false) => (
    <Field label={label} field={field} error={save.error} className={wide ? 'md:col-span-2' : undefined}>
      {(control) => <textarea {...control} className={areaClass} value={form[field] ?? ''} disabled={!editable} onChange={(e) => set(field, e.target.value || null)} />}
    </Field>
  )

  // What the server refuses an approval for. The dialog names each before the owner selects Approve.
  const draft = promise && revision?.status === 'draft' ? revision : null
  const blockers = draft
    ? [
        ...(edited !== null ? ['You have changes that are not saved. Save the draft, then read it again.'] : []),
        ...(draft.retires ? [] : NEEDED.filter((n) => !(form[n.field] ?? '').trim()).map((n) => `“${n.label}” is empty. An approved promise states it.`)),
        ...(promise!.missingLinks.length > 0 ? ['A linked part no longer exists in the model. Remove the link, then save.'] : []),
      ]
    : []

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader className="pr-8">
          <DialogTitle>
            {promise ? <><span className="font-mono">{promise.requirementId}</span> · {revision?.title}</> : 'New promise'}
          </DialogTitle>
          <DialogDescription>
            {promise === null
              ? 'State what must always hold. You approve it before it counts as a requirement.'
              : revision?.status === 'draft'
                ? revision.retires
                  ? `Revision ${revision.revision} retires this promise when an owner approves it. Revision ${revision.retiresRevision ?? promise.inForceRevision} stays in force until then.`
                  : `Revision ${revision.revision} is a draft.${promise.inForceRevision ? ` Revision ${promise.inForceRevision} stays in force until this one is approved.` : ' It is not a requirement until an owner approves it.'}`
                : promise.state === 'retired'
                  ? 'This promise is retired. It binds nothing.'
                  : `Revision ${revision?.revision} is approved and cannot be edited. To change it, start a new revision.`}
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
          {promise && revision?.status === 'draft' && isAgentActor(revision.lastChangedBy) && (
            <div role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
              <p className="font-medium">An agent token changed this draft after the last change by a person.</p>
              <p className="mt-0.5 text-muted-foreground">
                Token <span className="font-mono">{shortAgent(revision.lastChangedBy)}</span>
                {revision.lastChangedAt ? ` on ${new Date(revision.lastChangedAt).toLocaleString()}` : ''}.
                {promise.origin === 'human' ? ' A person wrote this promise first.' : ''} Read every field and every obligation before you approve. Settings, Agent tokens lists the token.
              </p>
            </div>
          )}
          <div className="grid gap-3 md:grid-cols-2">
            <Field label="Title" field="title" error={save.error} className="md:col-span-2">
              {(control) => <Input {...control} className="mt-1" value={form.title} onChange={(e) => set('title', e.target.value)} disabled={!editable} placeholder="An accepted order survives a broker outage" />}
            </Field>
            <Field label="Claim" field="claim" error={save.error} className="md:col-span-2">
              {(control) => (
                <textarea {...control} className={areaClass} value={form.claim} onChange={(e) => set('claim', e.target.value)} disabled={!editable} placeholder="When Orders accepts an order, the OrderCreated event reaches its consumers, also when the broker is down at that moment." />
              )}
            </Field>
            {area('scope', 'Scope')}
            {area('preconditions', 'Preconditions')}
            {area('expectedOutcome', 'Expected outcome')}
            {area('forbiddenOutcome', 'Forbidden outcome')}
            <ForbiddenEvents value={form.forbiddenEvents} onChange={(value) => set('forbiddenEvents', value)} references={references} disabled={!editable} error={save.error} />
            {area('evidenceCriteria', 'What counts as evidence', true)}
          </div>

          <section aria-labelledby="promise-pattern">
            <h3 id="promise-pattern" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Required source pattern</h3>
            <label className="mt-1.5 flex items-start gap-2 text-xs text-foreground">
              <input
                type="checkbox" className="mt-0.5 h-3.5 w-3.5 accent-[var(--primary)]" checked={form.requiresPattern !== null} disabled={!editable}
                onChange={(e) => set('requiresPattern', e.target.checked ? { repository: repositories[0] ?? '', pattern: 'transactional-outbox', service: null } : null)}
              />
              <span>
                The source of a repository must declare a pattern.
                <span className="block text-[11px] text-muted-foreground">“Generate from the links” then adds a source pattern check for it.</span>
              </span>
            </label>
            {form.requiresPattern && (
              <div className="mt-2">
                <PatternFields value={form.requiresPattern} onChange={(value) => set('requiresPattern', value)} repositories={repositories} references={references} disabled={!editable} error={save.error} prefix="requiresPattern" />
              </div>
            )}
          </section>

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
            {save.error && <div className="mt-1"><Failure error={save.error} shown={['title', 'claim', 'scope', 'preconditions', 'expectedOutcome', 'forbiddenOutcome', 'evidenceCriteria', 'forbiddenEvents', 'requiresPattern']} /></div>}
          </section>

          {promise && revision && !revision.retires && (
            <section aria-labelledby="promise-obligations">
              <div className="flex flex-wrap items-center justify-between gap-x-2">
                <h3 id="promise-obligations" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Obligations</h3>
                {editable && (
                  <div className="flex gap-1">
                    <Button variant="ghost" size="sm" className="h-6 text-xs" disabled={generate.isPending || edited !== null} onClick={() => generate.mutate()} title={edited ? 'Save your changes first' : undefined}>
                      Generate from the links
                    </Button>
                    <Button variant="ghost" size="sm" className="h-6 gap-1 text-xs" disabled={edited !== null} onClick={() => setEditing('new')} title={edited ? 'Save your changes first' : undefined}>
                      <Plus className="h-3 w-3" /> Add an obligation
                    </Button>
                  </div>
                )}
              </div>
              {revision.obligations.length === 0 && (
                <p className="mt-1 text-xs text-muted-foreground">No obligation yet. A promise without obligations has no evidence and never passes.</p>
              )}
              <ul className="mt-1.5 space-y-1.5">
                {revision.obligations.map((obligation) => {
                  const status = statusOf(obligation.id)
                  const stale = status?.current === false
                  const shown = describeResult(status, obligation.supported)
                  const source = obligation.recipe.sourcePattern
                  const candidates = source ? completed.filter((s) => s.repository.toLowerCase() === source.repository) : []
                  const scanId = scanFor[obligation.id] ?? candidates[0]?.id ?? null
                  return (
                    <li key={obligation.id} className="rounded-md border border-border px-3 py-2">
                      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                        <span className="font-mono text-[11px] text-muted-foreground">{obligation.id}</span>
                        <span className="min-w-0 flex-1 basis-40 text-sm text-foreground">{readable(obligation.title)}</span>
                        <span className="rounded-full border px-1.5 text-[11px]" style={{ borderColor: shown.color, color: shown.color }}>{shown.label}</span>
                        {editable && edited === null && (
                          <span className="flex gap-0.5">
                            <Button variant="ghost" size="sm" className="h-6 w-6 p-0" aria-label={`Change ${obligation.id}`} onClick={() => setEditing(obligation)}><Pencil className="h-3 w-3" /></Button>
                            <Button variant="ghost" size="sm" className="h-6 w-6 p-0" aria-label={`Remove ${obligation.id}`} disabled={removeObligation.isPending} onClick={() => removeObligation.mutate(obligation.key)}><Trash2 className="h-3 w-3" /></Button>
                          </span>
                        )}
                      </div>
                      <p className="mt-0.5 font-mono text-[10px] text-muted-foreground">
                        {isAgentActor(obligation.lastChangedBy ?? obligation.addedBy) && (
                          <span className="mr-1.5 rounded-full border border-[var(--trace-retry)]/60 px-1.5 text-[var(--trace-retry)]" title="An agent token wrote this obligation, or changed it last.">
                            {isAgentActor(obligation.lastChangedBy) && !isAgentActor(obligation.addedBy) ? 'changed by an agent' : 'by an agent'}
                          </span>
                        )}
                        {obligation.kind}{obligation.execution === 'external-evidence' ? ' · a test plan: Vellum cannot run it; it needs a result from your CI' : obligation.execution === 'none' ? ' · Vellum cannot check this kind' : ''}
                      </p>
                      <Recipe obligation={obligation} nameOf={nameOf} readable={readable} />
                      {obligation.execution === 'deterministic' && (
                        <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                          {source && (
                            candidates.length === 0
                              ? <span className="text-xs text-muted-foreground">No completed scan of {source.repository}. Import it, then run this check.</span>
                              : (
                                <select className={cn(smallSelect, 'h-6 w-auto max-w-full')} aria-label={`Scan that ${obligation.id} reads`} value={scanId ?? ''} onChange={(e) => setScanFor({ ...scanFor, [obligation.id]: e.target.value })}>
                                  {candidates.map((s) => <option key={s.id} value={s.id}>{s.ref ?? s.trackedRef ?? 'default branch'} · {s.commitSha?.slice(0, 10)} · {s.kind}</option>)}
                                </select>
                              )
                          )}
                          <Button variant="secondary" size="sm" className="h-6 text-xs" disabled={run.isPending || (source !== null && source !== undefined && scanId === null)} onClick={() => run.mutate({ key: obligation.key, scanId: source ? scanId : null })}>
                            {source ? 'Run against this scan' : 'Run'}
                          </Button>
                        </div>
                      )}
                      {obligation.recipe.plan && (
                        <PlanEvidence
                          key={obligation.id + (obligation.recipe.plan.evidenceSource ? JSON.stringify(obligation.recipe.plan.evidenceSource) : '')}
                          projectId={projectId} promiseId={promise.id} obligation={obligation} editable={Boolean(editable) && edited === null}
                          revision={revision.status === 'draft' ? revision.revision : null} onStored={store} onRecorded={reload}
                        />
                      )}
                      {status && (status.result !== 'not-run' || status.at) && (
                        <div className="mt-1.5 space-y-0.5 text-xs text-muted-foreground">
                          {stale && status.changed.length > 0 && (
                            <div className="break-words text-[var(--trace-retry)]">
                              <p>This result is stale. It was made before these changes:</p>
                              <ul className="list-disc pl-4">{status.changed.map((change, i) => <li key={i}>{readable(change)}</li>)}</ul>
                            </div>
                          )}
                          {status.scope && <p><span className="text-foreground">Checked:</span> {readable(status.scope)}</p>}
                          {status.environment && <p><span className="text-foreground">Against:</span> <span className="break-all font-mono">{status.environment}</span>{status.at ? ` on ${new Date(status.at).toLocaleString()}` : ''}</p>}
                          {status.modelRevision && (
                            <p><span className="text-foreground">Model:</span> version {status.modelRevision.version}{status.modelRevision.revisionName ? `, revision “${status.modelRevision.revisionName}”` : ''}</p>
                          )}
                          {status.commitSha && (
                            <p>
                              <span className="text-foreground">Commit:</span> <span className="font-mono">{status.commitSha.slice(0, 10)}</span>
                              {status.inputs?.ref ? <> of ref <span className="font-mono">{status.inputs.ref}</span></> : null}
                              {status.inputs?.scanKind ? `, a ${status.inputs.scanKind} scan` : ''}
                            </p>
                          )}
                          {status.effectiveFaults && status.effectiveFaults.length > 0 && (
                            <p><span className="text-foreground">Faults played:</span> {status.effectiveFaults.map((f) => `${f.kind} of ${nameOf(f.target)}${f.failFirst ? `, first ${f.failFirst} ${f.failFirst === 1 ? 'attempt' : 'attempts'}` : ', every attempt'}`).join('; ')}</p>
                          )}
                          {status.detail && <p>{readable(status.detail)}</p>}
                          {status.evidence.map((e, i) => (
                            <p key={i} className="break-all font-mono text-[11px]">
                              {e.kind === 'trace' ? `trace ${e.hash?.slice(0, 12)}` : e.kind === 'source' ? e.pointer : `external · ${e.identity}`}
                              {e.url && <> · <a className="underline-offset-2 hover:underline" href={e.url} target="_blank" rel="noreferrer noopener">open</a></>}
                            </p>
                          ))}
                          {status.assertions?.filter((a) => a.vacuous).map((a) => (
                            <p key={a.id}><span className="text-foreground">Checks nothing:</span> <span className="font-mono">{a.id}</span> · {a.vacuousReason && readable(a.vacuousReason)}</p>
                          ))}
                          {status.limitations.map((limitation, i) => <p key={i}><span className="text-foreground">Limit:</span> {readable(limitation)}</p>)}
                        </div>
                      )}
                      <RunHistory projectId={projectId} promiseId={promise.id} obligationId={obligation.id} nameOf={knownName} />
                    </li>
                  )
                })}
              </ul>
              {revision.evidenceStatus.gaps.length > 0 && (
                <div className="mt-2 rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs">
                  <p className="font-medium text-foreground">Evidence gaps</p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-muted-foreground">{revision.evidenceStatus.gaps.map((gap, i) => <li key={i}>{readable(gap)}</li>)}</ul>
                </div>
              )}
              <p className="mt-2 text-[11px] text-muted-foreground">A passed check is evidence for what it checked, in the scope shown. It does not prove the promise in general.</p>
            </section>
          )}
          {promise && revision && (
            <TargetThreads projectId={projectId} kind="promise" id={promise.id} name={`${promise.requirementId} ${revision.title}`} />
          )}
        </div>

        {failure && <Failure error={failure} />}
        {draft && (
          <div id="approval-blockers" className="rounded-md border border-border px-3 py-2 text-xs">
            {blockers.length > 0 ? (
              <>
                <p className="font-medium text-foreground">Approval is blocked:</p>
                <ul className="mt-0.5 list-disc pl-4 text-muted-foreground">{blockers.map((blocker) => <li key={blocker}>{blocker}</li>)}</ul>
              </>
            ) : (
              <p className="text-muted-foreground">
                Nothing on this screen blocks approval. Only a workspace owner approves, in a signed-in session. An approval binds exactly the content shown here.
              </p>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          {promise && revision && (
            <Button variant="ghost" size="sm" className="gap-1.5" disabled={show.isPending} onClick={() => show.mutate()}>
              <Crosshair className="h-3.5 w-3.5" /> Show on canvas
            </Button>
          )}
          {promise && promise.state === 'draft' && promise.inForceRevision === null && (
            <Button
              variant="ghost" size="sm" className="gap-1.5 text-destructive hover:text-destructive" disabled={remove.isPending}
              title="Delete this draft. It was never approved, so it binds nothing."
              onClick={() => { if (window.confirm(`Delete the draft promise ${promise.requirementId}? Its runs go with it. This cannot be undone.`)) remove.mutate() }}
            >
              <Trash2 className="h-3.5 w-3.5" /> Delete draft
            </Button>
          )}
          <div className="flex-1" />
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          {editable && (
            <Button variant="secondary" size="sm" disabled={save.isPending || (id !== null && edited === null) || !form.title.trim() || !form.claim.trim()} onClick={() => save.mutate()}>
              {id ? 'Save draft' : 'Create draft'}
            </Button>
          )}
          {draft && (
            <Button size="sm" disabled={approve.isPending || blockers.length > 0} aria-describedby="approval-blockers" onClick={() => approve.mutate()}>
              Approve revision {draft.revision}
            </Button>
          )}
          {promise && revision?.status === 'approved' && promise.state !== 'retired' && (
            <>
              <Button variant="ghost" size="sm" disabled={revise.isPending} onClick={() => revise.mutate(true)} title="Starts a revision that retires this promise. It retires when an owner approves that revision.">
                Retire
              </Button>
              <Button variant="secondary" size="sm" disabled={revise.isPending} onClick={() => revise.mutate(false)}>Start a new revision</Button>
            </>
          )}
        </div>

        {editing && promise && (
          <ObligationDialog
            projectId={projectId} promiseId={promise.id} obligation={editing === 'new' ? null : editing}
            forbiddenEvents={form.forbiddenEvents} references={references} scenarios={scenarios} repositories={repositories}
            onStored={store} onClose={() => setEditing(null)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

/** What an obligation checks, in words: the scenario and its faults, the pattern, or the plan. */
function Recipe({ obligation, nameOf, readable }: { obligation: Obligation; nameOf: (id: string) => string; readable: (text: string) => ReactNode }) {
  const { scenarioCheck, sourcePattern, plan, text } = obligation.recipe
  return (
    <div className="mt-1 space-y-0.5 text-xs text-muted-foreground">
      <p>{readable(text)}</p>
      {scenarioCheck && (
        <p>
          <span className="text-foreground">Faults:</span>{' '}
          {scenarioCheck.faults.length === 0
            ? 'none added to those of the scenario'
            : scenarioCheck.faults.map((f) => `${f.kind} of ${nameOf(f.target)}, ${f.failFirst ? `first ${f.failFirst} ${f.failFirst === 1 ? 'attempt' : 'attempts'}` : 'every attempt'}`).join('; ')}
          {' · '}<span className="text-foreground">{scenarioCheck.expect.length}</span> {scenarioCheck.expect.length === 1 ? 'assertion' : 'assertions'}
        </p>
      )}
      {sourcePattern && (
        <p>
          <span className="text-foreground">Pattern:</span> <span className="font-mono">{sourcePattern.pattern}</span> in <span className="font-mono">{sourcePattern.repository}</span>
          {sourcePattern.service ? `, service ${nameOf(sourcePattern.service)}` : ''}
          {sourcePattern.requireUnconditional === false ? ' · a declaration under a compile-time condition is accepted' : ''}
        </p>
      )}
      {plan && (
        <details>
          <summary className="cursor-pointer rounded text-foreground focus-visible:outline-2 focus-visible:outline-primary">
            Plan: {plan.steps.length} {plan.steps.length === 1 ? 'step' : 'steps'}, {plan.expectedObservations.length} expected, {plan.forbiddenObservations.length} forbidden
          </summary>
          <ol className="mt-1 list-decimal pl-5">{plan.steps.map((step, i) => <li key={i}>{readable(step)}</li>)}</ol>
          {plan.inputs.length > 0 && <p className="mt-1"><span className="text-foreground">Inputs:</span> {plan.inputs.join(' ')}</p>}
          <p className="mt-1"><span className="text-foreground">Expected:</span> {readable(plan.expectedObservations.join(' '))}</p>
          {plan.forbiddenObservations.length > 0 && <p className="mt-1"><span className="text-foreground">Forbidden:</span> {readable(plan.forbiddenObservations.join(' '))}</p>}
          {plan.contractIds.length > 0 && <p className="mt-1"><span className="text-foreground">Contracts:</span> {plan.contractIds.map(nameOf).join(', ')}</p>}
        </details>
      )}
    </div>
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

/**
 * A server text with the IDs in it replaced by names. An ID whose part is gone shows its first
 * eight characters, marked as an ID, with the full ID on hover.
 */
function Readable({ text, nameOf }: { text: string; nameOf: (id: string) => string | undefined }) {
  return (
    <>
      {readableSegments(text, nameOf).map((segment, i) =>
        segment.kind === 'id' ? (
          <abbr
            key={i}
            title={`ID ${segment.id}. No part of this model has this ID now.`}
            className="rounded bg-muted px-1 font-mono text-[0.92em] no-underline"
          >
            id:{segment.text}
          </abbr>
        ) : (
          <span key={i}>{segment.text}</span>
        ),
      )}
    </>
  )
}
