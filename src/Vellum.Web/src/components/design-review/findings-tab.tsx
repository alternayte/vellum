import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { areaClass, selectClass } from './styles'
import { SnapshotDialog } from './snapshot-dialog'
import { shownInline } from '@/lib/mutation-errors'
import {
  CATEGORY_LABEL, DEFAULT_FILTER, RESULT_COLOR, RESULT_LABEL, SEVERITY_LABEL, describeDisposition, filterFindings, groupFindings, reviewApi,
  type Category, type Cited, type Decision, type FindingFilter, type FindingView, type RuleResult, type Severity,
} from '@/lib/review-api'

interface Props {
  projectId: string
  branchId?: string | null
  /** The named revision that the workbench shows. The findings are of it. */
  revision?: { id: string; name: string } | null
  /** The element to show on the canvas for a cited part: the element itself, or the element that owns the part. */
  focusTarget: (cited: Cited) => string | null
  onFocus: (elementId: string) => void
}

/** A cited part. It is a link when the canvas has a node to show for it. */
export function CitedPart({ cited, focusTarget, onFocus }: { cited: Cited } & Pick<Props, 'focusTarget' | 'onFocus'>) {
  const target = focusTarget(cited)
  const label = <><span className="text-muted-foreground">{cited.kind}</span> {cited.name}</>
  return target ? (
    <button
      type="button" onClick={() => onFocus(target)} title="Show on the canvas"
      className="max-w-full truncate rounded border border-border px-1.5 py-0.5 text-left text-[11px] text-foreground underline decoration-dotted underline-offset-2 hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
    >
      {label}
    </button>
  ) : (
    <span className="max-w-full truncate rounded border border-border px-1.5 py-0.5 text-[11px] text-foreground">{label}</span>
  )
}

export function Toggle<T extends string>({ value, label, active, onToggle }: { value: T; label: string; active: boolean; onToggle: (value: T) => void }) {
  return (
    <button
      type="button" aria-pressed={active} onClick={() => onToggle(value)}
      className={cn(
        'rounded-full border px-2 py-0.5 text-[11px] focus-visible:outline-2 focus-visible:outline-primary',
        active ? 'border-primary text-foreground' : 'border-border text-muted-foreground hover:text-foreground',
      )}
    >
      {label}
    </button>
  )
}

function FindingCard({ view, decisions, projectId, branchId, revision, focusTarget, onFocus, onChanged }: Props & { view: FindingView; decisions: Decision[]; onChanged: () => void }) {
  const { finding } = view
  const [mode, setMode] = useState<'none' | 'wont-fix' | 'settle'>('none')
  const [reason, setReason] = useState('')
  const [decisionId, setDecisionId] = useState('')
  const set = useMutation({
    mutationFn: (request: { status: 'open' | 'accepted-by-decision' | 'wont-fix'; decisionId?: string; reason?: string }) =>
      reviewApi.setDisposition(projectId, branchId, finding, request.status, request, revision?.id ?? null),
    onSuccess: () => { setMode('none'); setReason(''); onChanged() },
    onError: shownInline,
  })
  const accepted = decisions.filter((d) => d.status === 'accepted')
  const disposition = describeDisposition(view)

  return (
    <li className="rounded-md border border-border px-2.5 py-2">
      <p className="flex flex-wrap items-center gap-1.5">
        <span className="rounded-full border px-1.5 text-[11px] font-medium" style={{ color: RESULT_COLOR[finding.result], borderColor: RESULT_COLOR[finding.result] }}>
          {RESULT_LABEL[finding.result]}
        </span>
        <CitedPart cited={finding.subject} focusTarget={focusTarget} onFocus={onFocus} />
        {view.blocks && <span className="rounded-full border border-[var(--trace-failure)] px-1.5 text-[11px] text-[var(--trace-failure)]">Blocks Build Ready</span>}
        {view.needsReReview && <span className="rounded-full border border-[var(--trace-retry)] px-1.5 text-[11px] text-[var(--trace-retry)]">Needs re-review</span>}
      </p>
      <p className="mt-1.5 text-xs leading-relaxed text-foreground">{finding.reason}</p>
      {finding.question && <p className="mt-1 text-xs font-medium text-foreground">{finding.question}</p>}
      {finding.cites.length > 0 && (
        <p className="mt-1.5 flex flex-wrap items-center gap-1">
          <span className="text-[11px] text-muted-foreground">Cites</span>
          {finding.cites.map((cited) => <CitedPart key={cited.id} cited={cited} focusTarget={focusTarget} onFocus={onFocus} />)}
        </p>
      )}
      <details className="mt-1.5 text-xs text-muted-foreground">
        <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">What would change this</summary>
        <p className="mt-1 leading-relaxed">{finding.wouldChange}</p>
      </details>

      {disposition && (
        <p className="mt-1.5 text-xs text-foreground">
          {disposition}
          {view.status === 'wont-fix' && view.reason ? <span className="text-muted-foreground"> · {view.reason}</span> : null}
        </p>
      )}
      {view.needsReReview && (
        <p className="mt-0.5 text-[11px] text-muted-foreground">The finding changed after it was answered. The answer settles nothing until a person reviews it again.</p>
      )}

      {(finding.fired || view.status !== 'open') && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {view.status === 'open' && finding.fired && (
            <>
              <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" onClick={() => setMode(mode === 'settle' ? 'none' : 'settle')}>Settle with a decision</Button>
              {finding.severity !== 'blocking' && (
                <Button variant="ghost" size="sm" className="h-6 px-2 text-xs" onClick={() => setMode(mode === 'wont-fix' ? 'none' : 'wont-fix')}>Won't fix</Button>
              )}
            </>
          )}
          {view.status === 'accepted-by-decision' && view.needsReReview && view.decisionId && (
            <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" disabled={set.isPending} onClick={() => set.mutate({ status: 'accepted-by-decision', decisionId: view.decisionId! })}>
              Settle again with {view.decision}
            </Button>
          )}
          {view.status === 'wont-fix' && view.needsReReview && (
            <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" onClick={() => { setReason(view.reason ?? ''); setMode('wont-fix') }}>Review again</Button>
          )}
          {view.status !== 'open' && (
            <Button variant="ghost" size="sm" className="h-6 px-2 text-xs" disabled={set.isPending} onClick={() => set.mutate({ status: 'open' })}>Set open</Button>
          )}
        </div>
      )}

      {mode === 'settle' && (
        <form className="mt-2 space-y-1.5" onSubmit={(e) => { e.preventDefault(); set.mutate({ status: 'accepted-by-decision', decisionId }) }}>
          {accepted.length === 0 ? (
            <p className="text-xs text-muted-foreground">No decision is accepted. Write a decision in the Decisions tab and accept it. Then settle the finding with it.</p>
          ) : (
            <>
              <label className="block text-[11px] font-medium text-muted-foreground">
                Accepted decision
                <select className={cn(selectClass, 'mt-1')} value={decisionId} onChange={(e) => setDecisionId(e.target.value)} required>
                  <option value="">Select a decision</option>
                  {accepted.map((d) => <option key={d.id} value={d.id}>{d.number} · {d.title}</option>)}
                </select>
              </label>
              <Button type="submit" size="sm" className="h-6 px-2 text-xs" disabled={set.isPending || !decisionId}>Settle</Button>
            </>
          )}
        </form>
      )}
      {mode === 'wont-fix' && (
        <form className="mt-2 space-y-1.5" onSubmit={(e) => { e.preventDefault(); set.mutate({ status: 'wont-fix', reason }) }}>
          <label className="block text-[11px] font-medium text-muted-foreground">
            Reason
            <textarea className={areaClass} value={reason} onChange={(e) => setReason(e.target.value)} required placeholder="Say why the finding stands." />
          </label>
          <Button type="submit" size="sm" className="h-6 px-2 text-xs" disabled={set.isPending || reason.trim() === ''}>Save</Button>
        </form>
      )}
      {set.isError && <p role="alert" className="mt-1.5 text-xs text-destructive">{set.error.message}</p>}
    </li>
  )
}

/**
 * The findings of the deterministic rules for the model as it is now. A finding says what
 * the declared model shows. It is not evidence about a running system.
 */
export function FindingsTab(props: Props) {
  const { projectId, branchId, revision } = props
  const revisionId = revision?.id ?? null
  const queryClient = useQueryClient()
  const findings = useQuery({
    queryKey: ['projects', projectId, 'review', 'findings', { branchId: branchId ?? null, revisionId }],
    queryFn: () => reviewApi.findings(projectId, branchId, revisionId), staleTime: 0,
  })
  const rules = useQuery({ queryKey: ['projects', projectId, 'review', 'rules'], queryFn: () => reviewApi.rules(projectId), staleTime: Infinity })
  const decisions = useQuery({ queryKey: ['projects', projectId, 'decisions'], queryFn: () => reviewApi.decisions(projectId) })
  const snapshots = useQuery({ queryKey: ['projects', projectId, 'review', 'snapshots'], queryFn: () => reviewApi.snapshots(projectId) })
  const [filter, setFilter] = useState<FindingFilter>(DEFAULT_FILTER)
  const [note, setNote] = useState('')
  const [openSnapshot, setOpenSnapshot] = useState<string | null>(null)
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review'] })
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'decisions'] })
    void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'build'] })
  }
  const save = useMutation({
    mutationFn: () => reviewApi.saveSnapshot(projectId, branchId, note.trim() || null, revisionId),
    onSuccess: () => { setNote(''); return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review', 'snapshots'] }) },
    onError: shownInline,
  })
  const removeSnapshot = useMutation({
    mutationFn: (id: string) => reviewApi.deleteSnapshot(projectId, id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review', 'snapshots'] }),
    onError: shownInline,
  })

  const toggle = <K extends keyof FindingFilter>(field: K) => (value: FindingFilter[K][number]) =>
    setFilter((f) => {
      const list = f[field] as string[]
      return { ...f, [field]: list.includes(value) ? list.filter((x) => x !== value) : [...list, value] }
    })
  const data = findings.data
  const shown = data ? filterFindings(data.findings, filter) : []
  const groups = groupFindings(shown)
  const titleOf = (ruleId: string) => rules.data?.find((r) => r.id === ruleId)?.title ?? ''

  return (
    <div className="space-y-3 p-3">
      {findings.isLoading && <p className="text-xs text-muted-foreground">Reading the model…</p>}
      {findings.isError && <p role="alert" className="text-xs text-destructive">{findings.error.message}</p>}
      {data && (
        <>
          <div className="rounded-md border border-border p-3">
            <p className="text-sm font-medium" style={{ color: data.counts.blocking > 0 ? 'var(--trace-retry)' : undefined }}>
              {data.counts.blocking > 0
                ? `${data.counts.blocking} ${data.counts.blocking === 1 ? 'finding blocks' : 'findings block'} Build Ready`
                : 'No finding blocks Build Ready'}
            </p>
            <p className="mt-1 text-xs text-muted-foreground">
              {data.counts.concerns} concerns · {data.counts.unknown} unknown · {data.counts.verified} verified · {data.counts.notApplicable} not applicable
              {data.counts.settled > 0 ? ` · ${data.counts.settled} settled` : ''}
              {data.counts.needsReReview > 0 ? ` · ${data.counts.needsReReview} need re-review` : ''}
            </p>
            <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
              A finding says what the declared model shows. “Verified” means the model satisfies the rule. It is not evidence about a running system. You can always save a draft.
            </p>
            <p className="mt-2 font-mono text-[10px] text-muted-foreground" title={data.hash}>
              {revision ? `revision ${revision.name} · ` : ''}model version {data.modelVersion} · {data.ruleSetVersion} · findings {data.hash.slice(0, 12)}
            </p>
          </div>

          <div className="space-y-1.5" aria-label="Filters">
            <p className="flex flex-wrap gap-1">
              {(Object.keys(RESULT_LABEL) as RuleResult[]).map((r) => <Toggle key={r} value={r} label={RESULT_LABEL[r]} active={filter.results.includes(r)} onToggle={toggle('results')} />)}
            </p>
            <p className="flex flex-wrap gap-1">
              {(Object.keys(SEVERITY_LABEL) as Severity[]).map((s) => <Toggle key={s} value={s} label={SEVERITY_LABEL[s]} active={filter.severities.includes(s)} onToggle={toggle('severities')} />)}
            </p>
            <p className="flex flex-wrap gap-1">
              {(Object.keys(CATEGORY_LABEL) as Category[]).map((c) => <Toggle key={c} value={c} label={CATEGORY_LABEL[c]} active={filter.categories.includes(c)} onToggle={toggle('categories')} />)}
            </p>
          </div>

          {groups.length === 0 && (
            <p className="text-xs text-muted-foreground">
              {data.findings.length === 0 ? 'The model has nothing for the rules to read yet.' : `No finding matches the filters. ${data.findings.length - shown.length} are hidden.`}
            </p>
          )}
          {groups.map((group) => (
            <section key={group.severity} aria-label={SEVERITY_LABEL[group.severity]}>
              <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
                {SEVERITY_LABEL[group.severity]} <span className="font-mono">{group.rules.reduce((n, r) => n + r.findings.length, 0)}</span>
              </h3>
              {group.rules.map((rule) => (
                <div key={rule.ruleId} className="mt-2">
                  <p className="text-xs text-foreground"><span className="font-mono">{rule.ruleId}</span> {titleOf(rule.ruleId)}</p>
                  <ul className="mt-1 space-y-1.5">
                    {rule.findings.map((view) => (
                      <FindingCard key={view.finding.ruleId + view.finding.subject.id} {...props} view={view} decisions={decisions.data ?? []} onChanged={refresh} />
                    ))}
                  </ul>
                </div>
              ))}
            </section>
          ))}

          <section aria-labelledby="review-snapshots">
            <h3 id="review-snapshots" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Snapshots</h3>
            <p className="mt-1 text-xs leading-relaxed text-muted-foreground">A snapshot keeps the findings of this model version with their answers. Open one to read it. A snapshot is a record, not the current state.</p>
            <form className="mt-1.5 flex gap-1.5" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
              <Input className="h-7 text-xs" value={note} onChange={(e) => setNote(e.target.value)} placeholder="Note (optional)" aria-label="Snapshot note" />
              <Button type="submit" size="sm" className="h-7 shrink-0 text-xs" disabled={save.isPending}>Save snapshot</Button>
            </form>
            {(save.error ?? removeSnapshot.error) && <p role="alert" className="mt-1 text-xs text-destructive">{(save.error ?? removeSnapshot.error)!.message}</p>}
            <ul className="mt-1.5 space-y-1">
              {snapshots.data?.map((snapshot) => (
                <li key={snapshot.id} className="flex items-baseline gap-2 text-[11px]">
                  <span className="shrink-0 font-mono text-muted-foreground" title={snapshot.hash}>v{snapshot.modelVersion} · {snapshot.hash.slice(0, 8)}</span>
                  <button
                    type="button" onClick={() => setOpenSnapshot(snapshot.id)} title="Open the stored snapshot"
                    className="min-w-0 flex-1 truncate rounded text-left text-foreground underline decoration-dotted underline-offset-2 hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                  >
                    {snapshot.note ?? new Date(snapshot.createdAt).toLocaleString()}
                  </button>
                  {snapshot.hash === data.hash && <span className="shrink-0 text-muted-foreground">same findings</span>}
                  <button type="button" className="shrink-0 rounded text-muted-foreground hover:text-destructive focus-visible:outline-2 focus-visible:outline-primary" onClick={() => removeSnapshot.mutate(snapshot.id)}>Delete</button>
                </li>
              ))}
            </ul>
            {snapshots.isSuccess && snapshots.data.length === 0 && <p className="mt-1.5 text-[11px] text-muted-foreground">No snapshot is stored.</p>}
          </section>
          {openSnapshot && (
            <SnapshotDialog
              projectId={projectId} snapshotId={openSnapshot} currentHash={data.hash}
              focusTarget={props.focusTarget} onFocus={props.onFocus} onClose={() => setOpenSnapshot(null)}
            />
          )}
        </>
      )}
    </div>
  )
}
