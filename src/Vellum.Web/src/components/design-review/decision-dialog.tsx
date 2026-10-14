import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { TargetThreads } from '@/components/comments/target-threads'
import { Input } from '@/components/ui/input'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { RESULT_LABEL, emptyDecision, modelLabel, reviewApi, type Decision, type DecisionInput, type FindingView } from '@/lib/review-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { areaClass, selectClass } from './styles'

interface Props {
  projectId: string
  branchId?: string | null
  /** The named revision that the workbench shows. The dialog lists and settles its findings. */
  revision?: { id: string; name: string } | null
  /** Null for a new decision. */
  decision: Decision | null
  /** For a new decision: the decision it replaces when it is accepted. */
  supersedes?: string | null
  decisions: Decision[]
  references: ScenarioReference[]
  onClose: () => void
  onSupersede: (decisionId: string) => void
}

function toInput(decision: Decision): DecisionInput {
  const { title, context, decision: text, alternatives, consequences, affectedIds, settles, supersedes } = decision
  return { title, context, decision: text, alternatives, consequences, affectedIds, settles, supersedes }
}

const STATUS_NOTE: Record<Decision['status'], string> = {
  proposed: 'This is a proposal. It decides nothing until an owner accepts it.',
  accepted: 'This decision is accepted and in force. Its text cannot be changed. To change it, write a decision that supersedes it.',
  superseded: 'An accepted decision replaced this one. It is not in force.',
  rejected: 'This decision is rejected. It is not in force.',
}

/**
 * One decision record (ADR). A person, an agent or the assistant can propose one. Only a
 * person with the Owner role accepts it, and only an accepted decision settles a finding.
 */
export function DecisionDialog({ projectId, branchId, revision = null, decision: initial, supersedes, decisions, references, onClose, onSupersede }: Props) {
  const queryClient = useQueryClient()
  const [decision, setDecision] = useState(initial)
  const [edited, setEdited] = useState<DecisionInput | null>(null)
  const [rejectReason, setRejectReason] = useState('')
  const [settleKey, setSettleKey] = useState('')
  const revisionId = revision?.id ?? null
  const model = modelLabel(branchId, revision?.name)
  const findings = useQuery({
    queryKey: ['projects', projectId, 'review', 'findings', { branchId: branchId ?? null, revisionId }],
    queryFn: () => reviewApi.findings(projectId, branchId, revisionId),
  })
  const editable = decision === null || decision.status === 'proposed'
  const form = edited ?? (decision ? toInput(decision) : emptyDecision(supersedes ?? null))
  const set = <K extends keyof DecisionInput>(field: K, value: DecisionInput[K]) => setEdited({ ...form, [field]: value })

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'decisions'] })
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review'] })
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'build'] })
  }
  const store = (next: Decision) => { setDecision(next); setEdited(null); refresh() }
  const save = useMutation({
    mutationFn: () => (decision ? reviewApi.updateDecision(projectId, decision.id, form) : reviewApi.createDecision(projectId, form)),
    onSuccess: store, onError: shownInline,
  })
  // Both name the content hash of the decision on the screen, so the server refuses a decision that changed since.
  // The acceptance records the findings of the model that this dialog lists them for.
  const accept = useMutation({ mutationFn: () => reviewApi.acceptDecision(projectId, decision!, branchId, revisionId), onSuccess: store, onError: shownInline })
  const reject = useMutation({ mutationFn: () => reviewApi.rejectDecision(projectId, decision!, rejectReason.trim() || null), onSuccess: store, onError: shownInline })
  const reload = useMutation({
    mutationFn: async () => (await reviewApi.decisions(projectId)).find((d) => d.id === decision!.id) ?? null,
    onSuccess: (fresh) => {
      accept.reset()
      reject.reset()
      if (fresh) store(fresh)
      else { refresh(); onClose() }
    },
    onError: shownInline,
  })
  const remove = useMutation({ mutationFn: () => reviewApi.deleteDecision(projectId, decision!.id), onSuccess: () => { refresh(); onClose() }, onError: shownInline })
  const settle = useMutation({
    mutationFn: (view: FindingView) => reviewApi.setDisposition(projectId, branchId, view.finding, 'accepted-by-decision', { decisionId: decision!.id }, revisionId),
    onSuccess: async () => {
      setSettleKey('')
      refresh()
      setDecision((await reviewApi.decisions(projectId)).find((d) => d.id === decision!.id) ?? decision)
    },
    onError: shownInline,
  })
  const failure = save.error ?? accept.error ?? reject.error ?? remove.error ?? settle.error ?? reload.error
  // 409: the decision is not the one on the screen any more, or its status does not allow the act.
  const conflict = [accept.error, reject.error].some((e) => e instanceof ApiError && e.status === 409)
  const findingOf = (ruleId: string, subjectId: string) =>
    findings.data?.findings.find((f) => f.finding.ruleId === ruleId && f.finding.subject.id === subjectId)

  const fired = (findings.data?.findings ?? []).filter((f) => f.finding.fired)
  const keyOf = (ruleId: string, subjectId: string) => `${ruleId}:${subjectId}`
  const subjectName = (ruleId: string, subjectId: string) =>
    findings.data?.findings.find((f) => f.finding.ruleId === ruleId && f.finding.subject.id === subjectId)?.finding.subject.name
    ?? references.find((r) => r.id === subjectId)?.label ?? subjectId
  const findingLabel = (ruleId: string, subjectId: string) => `${ruleId} · ${subjectName(ruleId, subjectId)}`
  const toggleSettles = (ruleId: string, subjectId: string) => {
    const has = form.settles.some((s) => s.ruleId === ruleId && s.subjectId === subjectId)
    set('settles', has ? form.settles.filter((s) => !(s.ruleId === ruleId && s.subjectId === subjectId)) : [...form.settles, { ruleId, subjectId }])
  }
  const toggleAffected = (id: string) =>
    set('affectedIds', form.affectedIds.includes(id) ? form.affectedIds.filter((x) => x !== id) : [...form.affectedIds, id])
  const setAlternative = (index: number, change: Partial<DecisionInput['alternatives'][number]>) =>
    set('alternatives', form.alternatives.map((a, i) => (i === index ? { ...a, ...change } : a)))
  const others = decisions.filter((d) => d.id !== decision?.id)
  const open = fired.filter((f) => f.status === 'open' || (f.decisionId === decision?.id && f.needsReReview))

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>
            {decision ? <><span className="font-mono">{decision.number}</span> · {decision.title}</> : 'New decision'}
          </DialogTitle>
          <DialogDescription>
            {decision ? `${STATUS_NOTE[decision.status]} Origin: ${decision.origin}.` : 'Record what was decided and why. An owner accepts it before it is in force.'}
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
          <label className="block text-xs font-medium text-muted-foreground">
            Title
            <Input className="mt-1" value={form.title} onChange={(e) => set('title', e.target.value)} disabled={!editable} placeholder="Billing consumes OrderCreated through a queue" />
          </label>
          <label className="block text-xs font-medium text-muted-foreground">
            Context
            <textarea className={areaClass} value={form.context} onChange={(e) => set('context', e.target.value)} disabled={!editable} placeholder="What makes this decision necessary." />
          </label>
          <label className="block text-xs font-medium text-muted-foreground">
            Decision
            <textarea className={areaClass} value={form.decision} onChange={(e) => set('decision', e.target.value)} disabled={!editable} placeholder="What is decided." />
          </label>
          <label className="block text-xs font-medium text-muted-foreground">
            Consequences
            <textarea className={areaClass} value={form.consequences ?? ''} onChange={(e) => set('consequences', e.target.value || null)} disabled={!editable} />
          </label>

          <section aria-labelledby="decision-alternatives">
            <div className="flex items-center justify-between">
              <h3 id="decision-alternatives" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Alternatives that were not chosen</h3>
              {editable && (
                <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={() => set('alternatives', [...form.alternatives, { title: '', whyNot: null }])}>Add</Button>
              )}
            </div>
            {form.alternatives.length === 0 && <p className="mt-1 text-xs text-muted-foreground">None recorded.</p>}
            <ul className="mt-1 space-y-1.5">
              {form.alternatives.map((alternative, i) => (
                <li key={i} className="grid gap-1.5 sm:grid-cols-[1fr_1.5fr_auto]">
                  <Input className="h-7 text-xs" value={alternative.title} onChange={(e) => setAlternative(i, { title: e.target.value })} disabled={!editable} placeholder="Option" aria-label={`Alternative ${i + 1}`} />
                  <Input className="h-7 text-xs" value={alternative.whyNot ?? ''} onChange={(e) => setAlternative(i, { whyNot: e.target.value || null })} disabled={!editable} placeholder="Why not" aria-label={`Why not alternative ${i + 1}`} />
                  {editable && <Button variant="ghost" size="sm" className="h-7 text-xs" onClick={() => set('alternatives', form.alternatives.filter((_, j) => j !== i))}>Remove</Button>}
                </li>
              ))}
            </ul>
          </section>

          <div className="grid gap-4 md:grid-cols-2">
            <fieldset className="min-w-0">
              <legend className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Affected parts</legend>
              <ul className="mt-1 max-h-36 space-y-0.5 overflow-y-auto">
                {references.filter((r) => editable || form.affectedIds.includes(r.id)).map((reference) => (
                  <li key={reference.id}>
                    <label className="flex items-center gap-1.5 text-xs text-foreground">
                      <input type="checkbox" className="h-3 w-3 accent-[var(--primary)]" checked={form.affectedIds.includes(reference.id)} disabled={!editable} onChange={() => toggleAffected(reference.id)} />
                      <span className="truncate">{reference.label}</span>
                    </label>
                  </li>
                ))}
              </ul>
              {!editable && form.affectedIds.length === 0 && <p className="mt-1 text-xs text-muted-foreground">None.</p>}
            </fieldset>

            <fieldset className="min-w-0">
              <legend className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Findings it answers</legend>
              {editable ? (
                <>
                  <ul className="mt-1 max-h-36 space-y-0.5 overflow-y-auto">
                    {fired.map(({ finding }) => (
                      <li key={keyOf(finding.ruleId, finding.subject.id)}>
                        <label className="flex items-center gap-1.5 text-xs text-foreground">
                          <input
                            type="checkbox" className="h-3 w-3 accent-[var(--primary)]"
                            checked={form.settles.some((s) => s.ruleId === finding.ruleId && s.subjectId === finding.subject.id)}
                            onChange={() => toggleSettles(finding.ruleId, finding.subject.id)}
                          />
                          <span className="truncate"><span className="font-mono">{finding.ruleId}</span> · {finding.subject.name}</span>
                        </label>
                      </li>
                    ))}
                  </ul>
                  {fired.length === 0 && <p className="mt-1 text-xs text-muted-foreground">The review has no concern and no unknown.</p>}
                  <p className="mt-1 text-[11px] text-muted-foreground">These are the findings of {model}. The decision settles the ones you select when an owner accepts it.</p>
                </>
              ) : (
                <ul className="mt-1 space-y-0.5 text-xs text-foreground">
                  {form.settles.length === 0 && <li className="text-muted-foreground">None.</li>}
                  {form.settles.map((s) => <li key={keyOf(s.ruleId, s.subjectId)} className="truncate">{findingLabel(s.ruleId, s.subjectId)}</li>)}
                </ul>
              )}
            </fieldset>
          </div>

          <label className="block text-xs font-medium text-muted-foreground">
            Supersedes
            <select className={`${selectClass} mt-1`} value={form.supersedes ?? ''} onChange={(e) => set('supersedes', e.target.value || null)} disabled={!editable}>
              <option value="">No decision</option>
              {others.map((d) => <option key={d.id} value={d.id}>{d.number} · {d.title}</option>)}
            </select>
          </label>

          {decision?.status === 'proposed' && (
            <section aria-labelledby="decision-accept-records" className="rounded-md border border-border p-2.5">
              <h3 id="decision-accept-records" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">What an acceptance records</h3>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                An acceptance records each finding below as it is now on {model}. The decision settles exactly that finding. When the finding changes, or is another one on another model, it needs a new review there.
              </p>
              {findings.isLoading && <p className="mt-1 text-xs text-muted-foreground">Reading the findings…</p>}
              {findings.isError && <p role="alert" className="mt-1 text-xs text-destructive">{findings.error.message}</p>}
              {findings.isSuccess && (
                <ul className="mt-1.5 space-y-1.5">
                  {decision.settles.length === 0 && <li className="text-xs text-muted-foreground">The decision names no finding, so an acceptance records none.</li>}
                  {decision.settles.map((s) => {
                    const found = findingOf(s.ruleId, s.subjectId)
                    return (
                      <li key={keyOf(s.ruleId, s.subjectId)} className="text-xs">
                        <p className="text-foreground">
                          <span className="font-mono">{s.ruleId}</span> · {subjectName(s.ruleId, s.subjectId)}
                          {found && <span className="text-muted-foreground"> · {RESULT_LABEL[found.finding.result]}</span>}
                        </p>
                        <p className="mt-0.5 leading-relaxed text-muted-foreground">
                          {found ? found.finding.reason : `The rule gives no finding for this subject on ${model}. The acceptance records that, and a finding that appears later needs a review.`}
                        </p>
                      </li>
                    )
                  })}
                </ul>
              )}
              {edited && <p className="mt-1.5 text-[11px] text-[var(--trace-retry)]">You have changes that are not saved. The list shows the saved decision. Save first, then accept.</p>}
            </section>
          )}

          {decision?.status === 'accepted' && (
            <section aria-labelledby="decision-settle">
              <h3 id="decision-settle" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Settle a finding of {model} with this decision</h3>
              {open.length === 0 ? (
                <p className="mt-1 text-xs text-muted-foreground">No open finding.</p>
              ) : (
                <form
                  className="mt-1 flex gap-1.5"
                  onSubmit={(e) => { e.preventDefault(); const view = open.find((f) => keyOf(f.finding.ruleId, f.finding.subject.id) === settleKey); if (view) settle.mutate(view) }}
                >
                  <select className={selectClass} value={settleKey} onChange={(e) => setSettleKey(e.target.value)} aria-label="Finding" required>
                    <option value="">Select a finding</option>
                    {open.map(({ finding }) => (
                      <option key={keyOf(finding.ruleId, finding.subject.id)} value={keyOf(finding.ruleId, finding.subject.id)}>{finding.ruleId} · {finding.subject.name}</option>
                    ))}
                  </select>
                  <Button type="submit" size="sm" className="h-7 shrink-0 text-xs" disabled={settle.isPending || !settleKey}>Settle</Button>
                </form>
              )}
            </section>
          )}

          {decision && (
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">History ({decision.history.length})</summary>
              <ul className="mt-1 space-y-0.5 font-mono text-[11px]">
                {decision.history.map((entry, i) => <li key={i}>{new Date(entry.at).toLocaleString()} · {entry.action}{entry.note ? ` · ${entry.note}` : ''}</li>)}
              </ul>
            </details>
          )}
          {decision && <TargetThreads projectId={projectId} kind="decision" id={decision.id} name={`${decision.number} ${decision.title}`} />}
        </div>

        {failure && (
          <div role="alert" className="text-xs text-destructive">
            <p>{failure.message}</p>
            {failure instanceof ApiError && failure.fields.length > 0 && (
              <ul className="mt-0.5 list-disc pl-4">{failure.fields.map((e, i) => <li key={i}>{e.field}: {e.message}</li>)}</ul>
            )}
            {conflict && (
              <p className="mt-1 flex flex-wrap items-center gap-2 text-foreground">
                Nothing was accepted or rejected. Read the decision as it is now, then decide again.
                <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" disabled={reload.isPending} onClick={() => reload.mutate()}>Reload the decision</Button>
              </p>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          {decision && (
            <a className="text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground" href={reviewApi.decisionExportUrl(projectId, decision.id)} download>
              Export as Markdown
            </a>
          )}
          <span className="flex-1" />
          {decision && decision.status !== 'rejected' && (
            <>
              <Input className="h-7 w-44 text-xs" value={rejectReason} onChange={(e) => setRejectReason(e.target.value)} placeholder="Reason (optional)" aria-label="Reason to reject" />
              <Button variant="ghost" size="sm" disabled={reject.isPending} onClick={() => reject.mutate()}>
                {decision.status === 'proposed' ? 'Reject' : 'Withdraw'}
              </Button>
            </>
          )}
          {decision && decision.acceptedAt === null && <Button variant="ghost" size="sm" disabled={remove.isPending} onClick={() => remove.mutate()}>Delete</Button>}
          {decision?.status === 'accepted' && <Button variant="secondary" size="sm" onClick={() => onSupersede(decision.id)}>Supersede</Button>}
          {editable && (
            <Button variant="secondary" size="sm" disabled={save.isPending || (decision !== null && edited === null)} onClick={() => save.mutate()}>
              {decision ? 'Save' : 'Create'}
            </Button>
          )}
          {decision?.status === 'proposed' && (
            <Button size="sm" disabled={accept.isPending || edited !== null || !findings.isSuccess} title={edited ? 'Save your changes first' : 'Needs the Owner role'} onClick={() => accept.mutate()}>Accept</Button>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
