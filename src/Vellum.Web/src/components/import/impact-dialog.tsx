import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Crosshair, FileText } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { BriefDialog } from '@/components/build/brief-dialog'
import { shownInline } from '@/lib/mutation-errors'
import { importApi, type ChangeImpact, type ImpactCertainty, type ImpactChange, type ImpactPromise, type ImpactReason, type ScanSummary } from '@/lib/import-api'
import { RESULT_COLOR, RESULT_LABEL, promisesApi } from '@/lib/promises-api'
import { promiseElementIds, scenarioElementIds } from '@/lib/scenario-parts'
import { useShellStore } from '@/stores/shell-store'
import { SourceLines } from './proposal-review'

const CERTAINTY: Record<ImpactCertainty, { label: string; meaning: string; className: string }> = {
  definite: { label: 'Definite', meaning: 'It stands on a contract diff or on source lines that this report cites.', className: 'border-foreground/70 text-foreground' },
  possible: { label: 'Possible', meaning: 'It is reached only through a flow or a service. Nothing shows that it depends on the change.', className: 'border-dashed border-[var(--trace-retry)] text-[var(--trace-retry)]' },
  unknown: { label: 'Unknown', meaning: 'The scan did not look where the answer is.', className: 'border-dotted border-muted-foreground text-muted-foreground' },
}

const STANDING: Record<ImpactPromise['standing'], { label: string; color: string; meaning: string }> = {
  broken: { label: 'Broken', color: 'var(--trace-failure)', meaning: 'A check of this promise failed against this scan.' },
  'at-risk': { label: 'At risk', color: 'var(--trace-retry)', meaning: 'The change touches something this promise names. No check failed against this scan.' },
  possible: { label: 'Possibly affected', color: 'var(--muted-foreground)', meaning: 'The promise is reached only through an affected flow.' },
  unknown: { label: 'Unknown', color: 'var(--muted-foreground)', meaning: 'The scan did not look where the answer is.' },
}

const ACTION: Record<string, { label: string; color: string; sign: string }> = {
  add: { label: 'Added', color: 'var(--trace-request)', sign: '+' },
  change: { label: 'Changed', color: 'var(--trace-retry)', sign: '~' },
  remove: { label: 'Removed', color: 'var(--trace-failure)', sign: '−' },
  conflict: { label: 'Conflict', color: 'var(--trace-event)', sign: '!' },
  relink: { label: 'Not matched', color: 'var(--trace-data)', sign: '?' },
}

const short = (sha: string | null) => sha?.slice(0, 10) ?? 'no commit'

function Certainty({ value }: { value: ImpactCertainty }) {
  return <span className={`shrink-0 rounded-full border px-1.5 text-[11px] ${CERTAINTY[value].className}`} title={CERTAINTY[value].meaning}>{CERTAINTY[value].label}</span>
}

function Reasons({ reasons }: { reasons: ImpactReason[] }) {
  return (
    <ul className="mt-1.5 space-y-1">
      {reasons.map((reason, i) => (
        <li key={i} className="flex items-start gap-2 text-xs text-muted-foreground">
          <Certainty value={reason.certainty} />
          <span className="min-w-0">{reason.text}</span>
        </li>
      ))}
    </ul>
  )
}

/**
 * What the source of one scan changes, and what that reaches: flows and promises, each
 * marked definite, possible or unknown. The report is a statement about the source. It is
 * not a result of a check, and it changes nothing.
 */
export function ImpactDialog({ projectId, scan, owners, onClose }: {
  projectId: string
  scan: ScanSummary
  /** The element that owns an operation, a subscription or a message: what the canvas shows for it. */
  owners: Record<string, string>
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const key = ['projects', projectId, 'imports', scan.id, 'impact']
  const loaded = useQuery({ queryKey: key, queryFn: () => importApi.impact(projectId, scan.id), staleTime: 0 })
  const impact = loaded.data
  const [briefing, setBriefing] = useState(false)

  const show = useMutation({
    mutationFn: async (target: { title: string; ids: () => Promise<string[]> }) => ({ title: target.title, elementIds: await target.ids() }),
    onSuccess: (highlight) => { useShellStore.getState().showOnCanvas(highlight); onClose() },
    onError: shownInline,
  })
  const run = useMutation({
    mutationFn: ({ promise, check }: { promise: ImpactPromise; check: string }) =>
      // A draft is run by its number. That run is labelled as a draft run and does not count after approval.
      promisesApi.run(projectId, promise.id, check, scan.id, promise.state === 'draft' ? promise.revision : null),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key })
      void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises'] })
    },
    onError: shownInline,
  })
  const failure = show.error ?? run.error

  const elementsOf = (impact: ChangeImpact, changeIds: string[]) =>
    impact.changes.filter((c) => changeIds.includes(c.id)).map((c) => c.elementId).filter((id): id is string => id !== null)
  const ref = scan.ref ?? scan.trackedRef ?? short(scan.commitSha)

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader className="pr-8">
          <DialogTitle>Impact of <span className="font-mono">{ref}</span> on {scan.repository}</DialogTitle>
          <DialogDescription>
            {impact
              ? <>Commit <span className="font-mono">{short(impact.head.commitSha)}</span> compared with {impact.base.scanId ? <><span className="font-mono">{impact.base.ref ?? 'a commit'}</span> at <span className="font-mono">{short(impact.base.commitSha)}</span></> : 'the model'}. The report reads the source. It runs no check and changes nothing.</>
              : 'What the source of this scan changes, and which flows and promises that reaches.'}
          </DialogDescription>
        </DialogHeader>

        {loaded.isLoading && <p className="text-sm text-muted-foreground">Reading the scan…</p>}
        {loaded.isError && <p role="alert" className="text-sm text-destructive">{loaded.error.message}</p>}

        {impact && (
          <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
            <div className="rounded-md border border-border px-3 py-2 text-xs">
              <dl className="grid gap-x-3 gap-y-1 sm:grid-cols-[auto_minmax(0,1fr)]">
                {(Object.keys(CERTAINTY) as ImpactCertainty[]).map((value) => (
                  <div key={value} className="contents">
                    <dt><Certainty value={value} /></dt>
                    <dd className="text-muted-foreground">{CERTAINTY[value].meaning}</dd>
                  </div>
                ))}
              </dl>
              <ul className="mt-2 list-disc space-y-0.5 pl-4 text-muted-foreground">
                {impact.limits.map((limit) => <li key={limit}>{limit}</li>)}
              </ul>
            </div>

            <section aria-labelledby="impact-changes">
              <h3 id="impact-changes" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Changed in the source <span className="font-mono">{impact.changes.length}</span></h3>
              {impact.changes.length === 0 && <p className="mt-1 text-xs text-muted-foreground">The source matches the model, and its source patterns match those of the base. Nothing is affected.</p>}
              <ul className="mt-1.5 space-y-1.5">
                {impact.changes.map((change) => (
                  <Change key={change.id} projectId={projectId} change={change} scanId={change.kind === 'source-pattern' && change.action === 'remove' ? (impact.base.scanId ?? scan.id) : scan.id} />
                ))}
              </ul>
            </section>

            <section aria-labelledby="impact-flows">
              <h3 id="impact-flows" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Affected flows <span className="font-mono">{impact.flows.length}</span></h3>
              {impact.flows.length === 0 && <p className="mt-1 text-xs text-muted-foreground">No stored scenario names anything that changed.</p>}
              <ul className="mt-1.5 space-y-1.5">
                {impact.flows.map((flow) => (
                  <li key={flow.scenarioId} className="rounded-md border border-border px-3 py-2">
                    <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                      <span className="min-w-0 flex-1 basis-40 text-sm text-foreground">{flow.name}</span>
                      <Certainty value={flow.certainty} />
                      <Button
                        variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" disabled={show.isPending}
                        onClick={() => show.mutate({
                          title: `Affected flow · ${flow.name}`,
                          ids: async () => [
                            ...await scenarioElementIds(projectId, flow.scenarioId, flow.stepIds.filter((s) => s !== 'fault' && !s.startsWith('assertion:')), owners),
                            ...elementsOf(impact, flow.reasons.map((r) => r.changeId)),
                          ],
                        })}
                      >
                        <Crosshair className="h-3 w-3" /> Show on canvas
                      </Button>
                    </div>
                    <Reasons reasons={flow.reasons} />
                  </li>
                ))}
              </ul>
            </section>

            <section aria-labelledby="impact-promises">
              <h3 id="impact-promises" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Affected promises <span className="font-mono">{impact.promises.length}</span></h3>
              {impact.promises.length === 0 && <p className="mt-1 text-xs text-muted-foreground">No promise names anything that changed.</p>}
              <ul className="mt-1.5 space-y-1.5">
                {impact.promises.map((promise) => {
                  const standing = STANDING[promise.standing]
                  return (
                    <li key={promise.id} className="rounded-md border border-border px-3 py-2">
                      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                        <span className="shrink-0 font-mono text-xs text-foreground">{promise.requirementId}</span>
                        <span className="min-w-0 flex-1 basis-40 text-sm text-foreground">{promise.title}</span>
                        <span className="shrink-0 rounded-full border px-1.5 text-[11px]" style={{ borderColor: standing.color, color: standing.color }} title={standing.meaning}>{standing.label}</span>
                        <Button
                          variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" disabled={show.isPending}
                          onClick={() => show.mutate({
                            title: `${promise.requirementId} · ${promise.title}`,
                            ids: async () => promiseElementIds(projectId, (await promisesApi.get(projectId, promise.id)).current, owners),
                          })}
                        >
                          <Crosshair className="h-3 w-3" /> Show on canvas
                        </Button>
                      </div>
                      <p className="mt-0.5 text-xs text-muted-foreground">
                        {standing.meaning}
                        {promise.state === 'draft' ? ` Revision ${promise.revision} is a draft. It is not a requirement until an owner approves it.` : ` Revision ${promise.revision} is approved.`}
                      </p>
                      <Reasons reasons={promise.reasons} />
                      {promise.checks.map((check) => (
                        <div key={check.obligationId} className="mt-2 rounded-md bg-muted/40 p-2 text-xs">
                          <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                            <span className="font-mono text-[11px] text-muted-foreground">{check.obligationId}</span>
                            <span className="min-w-0 flex-1 basis-40 text-foreground">{check.title}</span>
                            <span className="shrink-0 rounded-full border px-1.5 text-[11px]" style={{ borderColor: RESULT_COLOR[check.result], color: RESULT_COLOR[check.result] }}>{RESULT_LABEL[check.result]}</span>
                            <Button variant="secondary" size="sm" className="h-6 text-xs" disabled={run.isPending} onClick={() => run.mutate({ promise, check: check.key })}>
                              Run against this scan
                            </Button>
                          </div>
                          {check.detail && <p className="mt-1 text-muted-foreground">{check.detail}</p>}
                        </div>
                      ))}
                    </li>
                  )
                })}
              </ul>
              {impact.promisesNotAffected > 0 && (
                <p className="mt-1.5 text-xs text-muted-foreground">
                  {impact.promisesNotAffected} other {impact.promisesNotAffected === 1 ? 'promise names' : 'promises name'} nothing that changed. The report says nothing about {impact.promisesNotAffected === 1 ? 'it' : 'them'}.
                </p>
              )}
            </section>
          </div>
        )}

        {failure && <p role="alert" className="text-xs text-destructive">{failure.message}</p>}

        <div className="flex flex-wrap items-center justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button
            size="sm" className="gap-1.5" disabled={!impact || impact.changes.length === 0} onClick={() => setBriefing(true)}
            title={impact && impact.changes.length === 0 ? 'This scan changes nothing, so there is nothing to brief' : undefined}
          >
            <FileText className="h-3.5 w-3.5" /> Brief for this change
          </Button>
        </div>

        {briefing && (
          <BriefDialog
            projectId={projectId} scope={{ changeScanId: scan.id }} subject={`the change of ${ref}`}
            fileName={`brief-${scan.repository.replace(/[^a-z0-9]+/gi, '-')}-${short(scan.commitSha)}.md`} onClose={() => setBriefing(false)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

function Change({ projectId, change, scanId }: { projectId: string; change: ImpactChange; scanId: string }) {
  const [open, setOpen] = useState(false)
  const action = ACTION[change.action] ?? ACTION.change
  return (
    <li className="rounded-md border border-border px-3 py-2">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="flex h-4 w-4 shrink-0 items-center justify-center self-center rounded border font-mono text-[11px]" style={{ borderColor: action.color, color: action.color }} aria-hidden="true">{action.sign}</span>
        <span className="text-xs font-medium" style={{ color: action.color }}>{action.label}</span>
        <span className="min-w-0 flex-1 basis-40 text-sm text-foreground">
          {change.name} <span className="text-xs text-muted-foreground">{change.kind === 'source-pattern' ? `source pattern ${change.pattern}` : change.entityKind}</span>
        </span>
        <Certainty value={change.certainty} />
      </div>
      <p className="mt-1 text-xs text-muted-foreground">{change.basis}</p>
      {(change.diff.length > 0 || change.evidence.length > 0) && (
        <details className="mt-1.5 text-xs" onToggle={(e) => setOpen(e.currentTarget.open)}>
          <summary className="cursor-pointer rounded text-foreground focus-visible:outline-2 focus-visible:outline-primary">
            {[
              change.diff.length > 0 && `Diff of ${change.diff.map((d) => d.field).join(', ')}`,
              change.evidence.length > 0 && `${change.evidence.length} source ${change.evidence.length === 1 ? 'reference' : 'references'}: ${change.evidence[0].path}:${change.evidence[0].startLine}`,
            ].filter(Boolean).join(' · ')}
          </summary>
          {open && (
            <div className="mt-2 space-y-2">
              {change.diff.map((d) => (
                <div key={d.field} className="grid gap-2 sm:grid-cols-2">
                  {[{ side: 'In the model', value: d.from }, { side: 'In the source', value: d.to }].map(({ side, value }) => (
                    <figure key={side} className="min-w-0 overflow-hidden rounded-md border border-border">
                      <figcaption className="border-b border-border bg-muted/40 px-2 py-1 font-mono text-[10px] text-muted-foreground">{d.field} · {side}</figcaption>
                      {/* Repository text is shown as text. It is never rendered as markup. */}
                      <pre tabIndex={0} className="max-h-40 overflow-auto whitespace-pre-wrap break-all bg-background px-2 py-1.5 font-mono text-[11px] text-foreground focus-visible:outline-2 focus-visible:outline-primary">{value ?? 'empty'}</pre>
                    </figure>
                  ))}
                </div>
              ))}
              {change.evidence.map((evidence) => <SourceLines key={evidence.pointer} projectId={projectId} scanId={scanId} evidence={evidence} />)}
            </div>
          )}
        </details>
      )}
    </li>
  )
}
