import { useState } from 'react'
import { ApiError } from '@/lib/settings-api'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { shownInline } from '@/lib/mutation-errors'
import { usePersonName } from '@/hooks/use-person-name'
import { OpenQuestions } from './open-questions'
import { childLists, describeOrigin, describeStep, designApi, type FlowProposal, type FlowStep, type MissingEntry } from '@/lib/design-api'

interface Props {
  projectId: string
  proposal: FlowProposal
  labels: Record<string, string>
  onClose: () => void
  onAccepted: (scenarioId: string) => void
}

function describeMissing(entry: MissingEntry): string {
  const on = entry.owner ? ` on ${entry.owner.name}` : entry.channel && entry.consumer ? `: ${entry.consumer.name} on ${entry.channel.name}` : ''
  const kind = entry.elementKind ?? entry.transport ?? entry.channelKind ?? ''
  return `${entry.kind}${kind ? ` (${kind})` : ''}${on}`
}

/**
 * A flow the assistant proposes. It is a suggestion: nothing is saved until you accept it,
 * and every step refers to a part that exists in the model. Parts the flow needs and the
 * model lacks are listed apart; each one is created only when you ask.
 */
export function FlowProposalDialog({ projectId, proposal: initial, labels, onClose, onAccepted }: Props) {
  const queryClient = useQueryClient()
  const personOf = usePersonName(projectId)
  const [proposal, setProposal] = useState(initial)
  const refreshModel = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId] })

  const create = useMutation({
    mutationFn: (alias: string) => designApi.createMissing(projectId, proposal.id, alias),
    onSuccess: (next) => { setProposal(next); return refreshModel() },
    onError: shownInline,
  })
  const accept = useMutation({
    mutationFn: (acceptWithIssues: boolean) => designApi.acceptFlow(projectId, proposal.id, acceptWithIssues),
    onSuccess: async ({ scenarioId }) => { await refreshModel(); onAccepted(scenarioId) },
    onError: shownInline,
  })
  const reject = useMutation({
    mutationFn: () => designApi.rejectFlow(projectId, proposal.id),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'proposals'] }); onClose() },
    onError: shownInline,
  })
  // The model changed between the proposal and the accept: show what is true now and ask again.
  const wentStale = accept.error instanceof ApiError && accept.error.status === 409 && /changed|stale/i.test(accept.error.message)
  const reload = useMutation({ mutationFn: () => designApi.getFlow(projectId, proposal.id), onSuccess: (next) => { setProposal(next); accept.reset() }, onError: shownInline })
  const failure = create.error ?? (wentStale ? null : accept.error) ?? reject.error ?? reload.error

  // Names of parts created from this proposal are not in the page's label map yet.
  const allLabels = { ...labels, ...Object.fromEntries(proposal.missing.filter((m) => m.createdId).map((m) => [m.createdId!, m.name])) }
  const created = new Set(proposal.missing.filter((m) => m.createdId).map((m) => m.alias))
  const steps = proposal.scenario.steps
  // The author of the proposal, in the sentences below.
  const who = proposal.origin === 'ai' ? 'the assistant' : 'the agent'
  const canAccept = proposal.status === 'draft' && steps.length > 0 && !proposal.issues.some((i) => i.code === 'not-storable')

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{proposal.name || 'Proposed flow'}</DialogTitle>
          <DialogDescription>
            {describeOrigin(proposal)} It describes declared behaviour.{' '}
            {proposal.status === 'draft'
              ? 'It is not saved, and nothing is verified, until you decide.'
              : `${proposal.status === 'accepted' ? 'Accepted as a scenario' : 'Rejected'}${proposal.decidedBy ? ` by ${personOf(proposal.decidedBy)}` : ''}${proposal.decidedAt ? ` on ${new Date(proposal.decidedAt).toLocaleString()}` : ''}. A decided proposal does not change.`}
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1 text-sm">
          {proposal.summary && <p className="text-foreground">{proposal.summary}</p>}
          {(proposal.stale || wentStale) && (
            <div role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
              The model changed after {who} made this proposal.{' '}
              {wentStale ? 'Load the current check, then decide again.' : 'The steps and problems below are checked against the model as it is now.'}
              {wentStale && (
                <Button variant="secondary" size="sm" className="ml-2 h-6 text-xs" disabled={reload.isPending} onClick={() => reload.mutate()}>Load the current check</Button>
              )}
            </div>
          )}
          {proposal.omittedPrivate && proposal.omittedPrivate.count > 0 && (
            <p role="status" className="rounded-md border border-border px-3 py-2 text-xs text-muted-foreground">
              The assistant did not see {proposal.omittedPrivate.count} {proposal.omittedPrivate.count === 1 ? 'part' : 'parts'} of the model that came from a private repository. A workspace owner allows that under Settings, Privacy.
            </p>
          )}
          {proposal.resolvedToExisting.length > 0 && (
            <p className="text-xs text-muted-foreground">
              The proposal asked for {proposal.resolvedToExisting.map((r) => r.name).join(', ')}. The model has {proposal.resolvedToExisting.length === 1 ? 'it' : 'them'} already, so the flow uses the existing {proposal.resolvedToExisting.length === 1 ? 'part' : 'parts'}.
            </p>
          )}

          <section aria-labelledby="flow-steps">
            <h3 id="flow-steps" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Steps that can be saved <span className="font-mono">{countSteps(steps)}</span></h3>
            {steps.length === 0
              ? <p className="mt-1 text-xs text-muted-foreground">No step can be saved yet. Create the missing parts below, or reject the proposal.</p>
              : <StepList steps={steps} labels={allLabels} />}
            {proposal.preview && (
              <p className="mt-2 font-mono text-[11px] text-muted-foreground">
                Plays as {proposal.preview.eventCount} events · trace {proposal.preview.hash.slice(0, 12)}
              </p>
            )}
          </section>

          {proposal.missing.length > 0 && (
            <section aria-labelledby="flow-missing">
              <h3 id="flow-missing" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Parts the flow needs and the model lacks</h3>
              <ul className="mt-1.5 space-y-1.5">
                {proposal.missing.map((entry) => {
                  const waitsFor = entry.createAfter.filter((alias) => !created.has(alias))
                  return (
                    <li key={entry.alias} className="flex items-start gap-3 rounded-md border border-border px-3 py-2">
                      <div className="min-w-0 flex-1">
                        <p className="text-sm text-foreground">{entry.name} <span className="font-mono text-[10px] text-muted-foreground">{describeMissing(entry)}</span></p>
                        <p className="mt-0.5 text-xs text-muted-foreground">{entry.reason}</p>
                        {waitsFor.length > 0 && (
                          <p className="mt-0.5 text-xs text-muted-foreground">
                            Create {waitsFor.map((alias) => proposal.missing.find((m) => m.alias === alias)?.name ?? alias).join(', ')} first.
                          </p>
                        )}
                      </div>
                      {entry.createdId ? (
                        <span className="shrink-0 text-xs text-[var(--trace-request)]">Created as inferred</span>
                      ) : (
                        <Button size="sm" variant="secondary" className="h-7 shrink-0 text-xs" disabled={create.isPending || waitsFor.length > 0 || proposal.status !== 'draft'} onClick={() => create.mutate(entry.alias)}>
                          Create
                        </Button>
                      )}
                    </li>
                  )
                })}
              </ul>
              <p className="mt-1.5 text-xs text-muted-foreground">A created part is added to the model with the label “inferred” and this proposal as its source.</p>
              {proposal.blockedSteps.length > 0 && (
                <p className="mt-1 text-xs text-[var(--trace-retry)]">
                  {proposal.blockedSteps.length} {proposal.blockedSteps.length === 1 ? 'step waits' : 'steps wait'} for these parts and {proposal.blockedSteps.length === 1 ? 'is' : 'are'} not in the list above.
                </p>
              )}
            </section>
          )}

          {proposal.rejectedReferences.length > 0 && (
            <section aria-labelledby="flow-dropped">
              <h3 id="flow-dropped" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Left out</h3>
              <p className="mt-1 text-xs text-muted-foreground">The proposal referred to things that do not exist. Vellum left those parts out.</p>
              <ul className="mt-1 space-y-0.5 font-mono text-[11px] text-muted-foreground">
                {proposal.rejectedReferences.map((r, i) => <li key={i}>{r.where}: {r.reason}</li>)}
              </ul>
            </section>
          )}

          {proposal.issues.length > 0 && (
            <section aria-labelledby="flow-issues">
              <h3 id="flow-issues" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Problems the trace check found</h3>
              <ul className="mt-1 space-y-0.5 text-xs">
                {proposal.issues.map((issue, i) => (
                  <li key={i} className="flex gap-2"><span className="shrink-0 font-mono text-[var(--trace-failure)]">{issue.stepId || 'flow'}</span><span>{issue.message}</span></li>
                ))}
              </ul>
              <p className="mt-1 text-xs text-muted-foreground">You can accept the flow and correct these in the scenario editor.</p>
            </section>
          )}

          {proposal.openQuestions.length > 0 && (
            <section aria-labelledby="flow-questions">
              <h3 id="flow-questions" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Questions {who} could not answer</h3>
              <OpenQuestions projectId={projectId} proposalId={proposal.id} questions={proposal.openQuestions} />
            </section>
          )}

          {proposal.reasoningSummary && (
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">Why {who} proposed this</summary>
              <p className="mt-1 leading-relaxed">{proposal.reasoningSummary}</p>
            </details>
          )}
        </div>

        {failure && <p role="alert" className="text-xs text-destructive">{failure.message}</p>}

        <div className="flex flex-wrap items-center gap-2">
          <div className="flex-1" />
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          {proposal.status === 'draft' && (
            <>
              <Button variant="secondary" size="sm" disabled={reject.isPending} onClick={() => reject.mutate()}>Reject</Button>
              <Button size="sm" disabled={!canAccept || accept.isPending || wentStale} onClick={() => accept.mutate(proposal.issues.length > 0)}>
                {accept.isPending ? 'Saving…' : proposal.issues.length > 0 ? 'Accept with these problems' : 'Accept and play'}
              </Button>
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

function countSteps(steps: FlowStep[]): number {
  return steps.reduce((total, step) => total + 1 + childLists(step).reduce((sum, list) => sum + countSteps(list.steps), 0), 0)
}

function StepList({ steps, labels }: { steps: FlowStep[]; labels: Record<string, string> }) {
  return (
    <ol className="mt-1.5 space-y-1">
      {steps.map((step) => (
        <li key={step.id}>
          <p className="flex items-baseline gap-2">
            <span className="shrink-0 font-mono text-[10px] text-muted-foreground">{step.id}</span>
            <span className="text-sm text-foreground">{describeStep(step, labels)}</span>
          </p>
          {childLists(step).filter((list) => list.steps.length > 0).map((list) => (
            <div key={list.label} className="ml-3 mt-1 border-l border-border pl-3">
              <p className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{labels[list.label.replace('consumer ', '')] ? `consumer ${labels[list.label.replace('consumer ', '')]}` : list.label}</p>
              <StepList steps={list.steps} labels={labels} />
            </div>
          ))}
        </li>
      ))}
    </ol>
  )
}
