import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, Crosshair, FileText, Play } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { BriefDialog } from '@/components/build/brief-dialog'
import { fetchScenario, useRunScenario } from '@/hooks/use-scenarios'
import { shownInline } from '@/lib/mutation-errors'
import { cn, prefersReducedMotion } from '@/lib/utils'
import { selectClass } from '@/components/settings/fields'
import { prettyJson } from '@/lib/pretty-json'
import { importApi, pullRequestState, scanSubject, type ChangeImpact, type ImpactGate, type ImpactGatePolicy, type ImpactCertainty, type ImpactChange, type ImpactFlow, type ImpactPromise, type ImpactReason, type ImpactStep, type ScanSummary } from '@/lib/import-api'
import { CERTAINTY, flowStepImpact, strongest } from '@/lib/impact'
import { RESULT_COLOR, RESULT_LABEL, promisesApi } from '@/lib/promises-api'
import { promiseElementIds, scenarioSteps, stepEnds } from '@/lib/scenario-parts'
import { useShellStore, type CanvasHighlight, type HighlightStep } from '@/stores/shell-store'
import { useTraceStore } from '@/stores/trace-store'
import { SourceLines } from './proposal-review'

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

const VERDICT: Record<ImpactGate['verdict'], { label: string; color: string }> = {
  pass: { label: 'Pass', color: 'var(--trace-request)' },
  fail: { label: 'Fail', color: 'var(--trace-failure)' },
  off: { label: 'Off', color: 'var(--muted-foreground)' },
}

const POLICIES: { id: ImpactGatePolicy; label: string }[] = [
  { id: 'off', label: 'Off: never fails' },
  { id: 'fail-on-broken', label: 'Fail on a broken approved promise' },
]

/**
 * The gate policy of the project and its verdict on this scan. Only an approved promise
 * that a deterministic check broke against this scan fails it; nothing else does. Only an
 * owner changes the policy. Vellum sends the verdict nowhere: a CI job reads the report.
 */
function GateSection({ projectId, impactKey, reportKey, scanId }: { projectId: string; impactKey: unknown[]; reportKey: unknown[]; scanId: string }) {
  const queryClient = useQueryClient()
  const settingKey = ['projects', projectId, 'impact-gate']
  const setting = useQuery({ queryKey: settingKey, queryFn: () => importApi.gate(projectId) })
  const report = useQuery({ queryKey: reportKey, queryFn: () => importApi.report(projectId, scanId), staleTime: 0 })
  const change = useMutation({
    mutationFn: (policy: ImpactGatePolicy) => importApi.setGate(projectId, policy),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: settingKey })
      void queryClient.invalidateQueries({ queryKey: reportKey })
    },
    onError: shownInline,
  })
  // Each result is for this scan only. It leaves the promise as it is against the ref the model follows.
  const runChecks = useMutation({
    mutationFn: () => importApi.runChecks(projectId, scanId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: impactKey })
      void queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises'] })
    },
    onError: shownInline,
  })
  const gate = report.data?.gate
  const policy = setting.data?.policy ?? gate?.policy ?? 'off'
  return (
    <section aria-labelledby="impact-gate" className="rounded-md border border-border px-3 py-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <h3 id="impact-gate" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Gate</h3>
        {gate
          ? <span className="rounded-full border px-1.5 text-[11px]" style={{ borderColor: VERDICT[gate.verdict].color, color: VERDICT[gate.verdict].color }}>{VERDICT[gate.verdict].label}</span>
          : <span className="text-xs text-muted-foreground">{report.isError ? report.error.message : 'Reading…'}</span>}
        <Button
          variant="secondary" size="sm" className="ml-auto h-7 gap-1 text-xs" disabled={runChecks.isPending} onClick={() => runChecks.mutate()}
          title="Run the source pattern checks of the approved promises against this scan. The results count for this scan only."
        >
          <Play className="h-3 w-3" /> {runChecks.isPending ? 'Running checks…' : 'Run checks against this scan'}
        </Button>
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          Policy
          <select
            className={cn(selectClass, 'w-auto')} value={policy} disabled={!setting.data?.canChange || change.isPending}
            aria-describedby="impact-gate-who"
            onChange={(e) => change.mutate(e.target.value as ImpactGatePolicy)}
          >
            {POLICIES.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </select>
        </label>
      </div>
      {gate && (
        <ul className="mt-1.5 list-disc space-y-0.5 pl-4 text-xs text-muted-foreground">
          {gate.reasons.map((reason) => <li key={reason}>{reason}</li>)}
        </ul>
      )}
      <p id="impact-gate-who" className="mt-1.5 text-xs text-muted-foreground">
        {setting.data?.canChange
          ? 'Possible, unknown and at-risk entries never fail the gate. A CI job reads the verdict from the report; Vellum writes nothing to GitHub.'
          : 'Only an owner changes the policy. A CI job reads the verdict from the report; Vellum writes nothing to GitHub.'}
      </p>
      {runChecks.data && (
        <p role="status" className="mt-1 text-xs text-muted-foreground">
          {runChecks.data.runs.length === 0
            ? 'No approved promise has a source pattern check of this repository.'
            : `Ran ${runChecks.data.runs.length} ${runChecks.data.runs.length === 1 ? 'check' : 'checks'}: ${runChecks.data.runs.map((r) => `${r.requirementId} ${r.key} ${r.result === 'unsupported' ? 'unknown' : r.result}`).join(', ')}.`}
        </p>
      )}
      {change.isError && <p role="alert" className="mt-1 text-xs text-destructive">{change.error.message}</p>}
      {runChecks.isError && <p role="alert" className="mt-1 text-xs text-destructive">{runChecks.error.message}</p>}
    </section>
  )
}

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
/**
 * An affected flow as the canvas shows it: each affected step from where it starts to what
 * it reaches, in the certainty of the step, and each element in the certainty of the surest
 * step or change that reaches it.
 */
async function flowHighlight(projectId: string, impact: ChangeImpact, flow: ImpactFlow, owners: Record<string, string>): Promise<CanvasHighlight> {
  const parts = new Map(scenarioSteps((await fetchScenario(projectId, flow.scenarioId)).steps).map((p) => [p.id, p]))
  const steps: HighlightStep[] = []
  const marks: Record<string, ImpactCertainty[]> = {}
  const mark = (id: string | null | undefined, certainty: ImpactCertainty) => { if (id) (marks[id] ??= []).push(certainty) }
  flow.steps.forEach((step, i) => {
    const part = parts.get(step.stepId)
    if (!part) return
    const ends = stepEnds(part, owners)
    steps.push({ stepId: step.stepId, order: i + 1, label: step.label, certainty: step.certainty, fromId: ends.fromId, toIds: ends.toIds, messageId: part.messageId })
    mark(ends.fromId, step.certainty)
    ends.toIds.forEach((id) => mark(id, step.certainty))
  })
  for (const reason of flow.reasons) {
    const change = impact.changes.find((c) => c.id === reason.changeId)
    mark(change?.elementId, change?.certainty ?? 'unknown')
  }
  return {
    title: `Affected flow · ${flow.name}`,
    elementIds: Object.keys(marks),
    marks: Object.fromEntries(Object.entries(marks).map(([id, values]) => [id, strongest(values)])),
    steps,
  }
}

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
  const reportKey = [...key, 'report']
  // The Markdown is read before the click, so the copy happens in the click itself.
  const markdown = useQuery({ queryKey: [...key, 'report', 'markdown'], queryFn: () => importApi.reportMarkdown(projectId, scan.id), staleTime: 0, enabled: !!impact })
  const [copied, setCopied] = useState<'done' | 'failed' | null>(null)
  const copyReport = async () => {
    try {
      await navigator.clipboard.writeText(markdown.data!)
      setCopied('done')
    } catch {
      setCopied('failed')
    }
  }

  const show = useMutation({
    mutationFn: async (target: { title: string; ids: () => Promise<string[]> }) => ({ title: target.title, elementIds: await target.ids() }),
    onSuccess: (highlight) => { useShellStore.getState().showOnCanvas(highlight); onClose() },
    onError: shownInline,
  })
  const play = useRunScenario(projectId)
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
  const showFlow = useMutation({
    mutationFn: ({ impact, flow }: { impact: ChangeImpact; flow: ImpactFlow }) => flowHighlight(projectId, impact, flow, owners),
    onSuccess: (highlight) => { useShellStore.getState().showOnCanvas(highlight); onClose() },
    onError: shownInline,
  })
  const failure = show.error ?? showFlow.error ?? run.error ?? play.error
  const ref = scanSubject(scan)

  const playFlow = (impact: ChangeImpact, flow: ImpactFlow) =>
    play.mutate(flow.scenarioId, {
      onSuccess: (result) => {
        useTraceStore.getState().load(result, flow.scenarioId, { change: `${ref} on ${scan.repository}`, steps: flowStepImpact(impact, flow) })
        useShellStore.getState().clearCanvasHighlight()
        useShellStore.getState().closeFlyout()
        onClose()
      },
    })

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader className="pr-8">
          <DialogTitle>Impact of <span className="font-mono">{ref}</span> on {scan.repository}</DialogTitle>
          {impact?.pullRequest && (
            <p className="text-sm text-foreground">
              {/* The title is repository text, shown as text. */}
              <span className="font-mono">#{impact.pullRequest.number}</span> {impact.pullRequest.title ?? 'No title'}
              <span className="ml-2 rounded-full border border-border px-1.5 text-[11px] text-muted-foreground">{pullRequestState(impact.pullRequest)}</span>
              <span className="block font-mono text-[11px] text-muted-foreground">
                {impact.pullRequest.headRef} {short(impact.pullRequest.headSha)} → {impact.pullRequest.baseRef} {short(impact.pullRequest.baseSha)}
                {impact.pullRequest.fromFork && ` · from ${impact.pullRequest.headRepository ?? 'a deleted fork'}`}
              </span>
            </p>
          )}
          <DialogDescription>
            {impact
              ? <>Commit <span className="font-mono">{short(impact.head.commitSha)}</span> compared with {impact.base.scanId ? <><span className="font-mono">{impact.base.ref ?? 'a commit'}</span> at <span className="font-mono">{short(impact.base.commitSha)}</span></> : 'the model'}. The report reads the source and changes nothing in the model. Checks run only when you select Run checks against this scan, and their results count for this scan only.</>
              : 'What the source of this scan changes, and which flows and promises that reaches.'}
          </DialogDescription>
        </DialogHeader>

        {loaded.isLoading && <p className="text-sm text-muted-foreground">Reading the scan…</p>}
        {loaded.isError && <p role="alert" className="text-sm text-destructive">{loaded.error.message}</p>}

        {impact && (
          <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
            <GateSection projectId={projectId} impactKey={key} reportKey={reportKey} scanId={scan.id} />
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
                  <Change key={change.id} anchor={changeAnchor(change.id)} projectId={projectId} change={change} contentHeld={scan.contentHeld} scanId={change.kind === 'source-pattern' && change.action === 'remove' ? (impact.base.scanId ?? scan.id) : scan.id} />
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
                        variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" disabled={showFlow.isPending}
                        title="Draw the affected steps on the canvas, each in its certainty"
                        onClick={() => showFlow.mutate({ impact, flow })}
                      >
                        <Crosshair className="h-3 w-3" /> Show on canvas
                      </Button>
                      <Button
                        variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" disabled={play.isPending}
                        title="Play the flow. The trace marks each affected step."
                        onClick={() => playFlow(impact, flow)}
                      >
                        <Play className="h-3 w-3" /> Play
                      </Button>
                    </div>
                    <FlowSteps impact={impact} steps={flow.steps} />
                    <details className="mt-1.5 text-xs">
                      <summary className="cursor-pointer rounded text-muted-foreground focus-visible:outline-2 focus-visible:outline-primary">
                        {flow.reasons.length} {flow.reasons.length === 1 ? 'reason' : 'reasons'} for the flow
                      </summary>
                      <Reasons reasons={flow.reasons} />
                    </details>
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
          <p className="min-w-0 flex-1 text-xs text-muted-foreground" role="status">
            {copied === 'done' ? 'The report is on the clipboard as Markdown.' : copied === 'failed' ? 'The browser did not allow the copy.' : markdown.isError ? markdown.error.message : ''}
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button
            variant="secondary" size="sm" className="gap-1.5" disabled={!markdown.data} onClick={() => void copyReport()}
            title="The compact report as a CI job reads it: Markdown, with the gate verdict"
          >
            <Copy className="h-3.5 w-3.5" /> Copy report
          </Button>
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

/** The ID of a change's entry in the report, so a step can link to the change and its evidence. */
const changeAnchor = (changeId: string) => `impact-change-${changeId.replace(/[^a-zA-Z0-9_-]/g, '-')}`

/**
 * The affected steps of a flow in the order the scenario writes them. Each names its
 * certainty and the changes it stands on, with a link to each change and its source lines.
 */
function FlowSteps({ impact, steps }: { impact: ChangeImpact; steps: ImpactStep[] }) {
  const changes = new Map(impact.changes.map((c) => [c.id, c]))
  return (
    <ol aria-label="Affected steps" className="mt-2 space-y-1 border-l border-border pl-3">
      {steps.map((step) => {
        const byChange = new Map<string, ImpactCertainty>()
        for (const reason of step.reasons) byChange.set(reason.changeId, strongest([reason.certainty, byChange.get(reason.changeId) ?? 'unknown']))
        return (
          <li key={step.stepId} className="text-xs">
            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
              <span
                aria-hidden="true" className="h-0 w-4 shrink-0 self-center border-t-2"
                style={{ borderColor: CERTAINTY[step.certainty].color, borderTopStyle: CERTAINTY[step.certainty].line }}
              />
              <span className="font-mono text-[11px] text-foreground">{step.stepId}</span>
              <span className="text-muted-foreground">{step.kind}{step.label ? ` · ${step.label}` : ''}</span>
              <Certainty value={step.certainty} />
            </div>
            <ul className="ml-6 mt-0.5 space-y-0.5">
              {[...byChange].map(([changeId, certainty]) => {
                const change = changes.get(changeId)
                const evidence = change?.evidence[0]
                return (
                  <li key={changeId} className="text-muted-foreground">
                    <span style={{ color: CERTAINTY[certainty].color }}>{CERTAINTY[certainty].label}</span>
                    {' through '}
                    <button
                      type="button"
                      className="text-left text-foreground underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary"
                      onClick={() => {
                        // The change's entry holds its diff and source lines. It opens and takes the focus.
                        const entry = document.getElementById(changeAnchor(changeId))
                        entry?.querySelector('details')?.setAttribute('open', '')
                        entry?.scrollIntoView({ block: 'center', behavior: prefersReducedMotion() ? 'auto' : 'smooth' })
                        entry?.focus({ preventScroll: true })
                      }}
                    >
                      {change ? `${(ACTION[change.action] ?? ACTION.change).label.toLowerCase()} ${change.kind === 'source-pattern' ? 'pattern' : change.entityKind} ${change.name}` : changeId}
                    </button>
                    {evidence && (
                      <>
                        {' · '}
                        <a href={evidence.url ?? undefined} target="_blank" rel="noreferrer noopener" className="font-mono underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary" title={evidence.pointer}>
                          {evidence.path}:{evidence.startLine}
                        </a>
                      </>
                    )}
                    {!evidence && ' · no source lines'}
                  </li>
                )
              })}
            </ul>
          </li>
        )
      })}
    </ol>
  )
}

function Change({ projectId, change, scanId, contentHeld, anchor }: { projectId: string; change: ImpactChange; scanId: string; contentHeld: boolean; anchor: string }) {
  const [open, setOpen] = useState(false)
  const action = ACTION[change.action] ?? ACTION.change
  return (
    <li id={anchor} tabIndex={-1} className="scroll-mt-2 rounded-md border border-border px-3 py-2 focus:border-primary focus:outline-none">
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
                      <pre tabIndex={0} className={`max-h-40 overflow-auto bg-background px-2 py-1.5 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary ${prettyJson(value) === null ? 'whitespace-pre-wrap break-all' : ''}`}>{prettyJson(value) ?? value ?? 'empty'}</pre>
                    </figure>
                  ))}
                </div>
              ))}
              {change.evidence.map((evidence) => <SourceLines key={evidence.pointer} projectId={projectId} scanId={scanId} contentHeld={contentHeld} evidence={evidence} />)}
            </div>
          )}
        </details>
      )}
    </li>
  )
}
