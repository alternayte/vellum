import { Button } from '@/components/ui/button'
import { withDependencies, type DecisionResult } from '@/lib/import-api'
import type { StaleMark } from '@/lib/fact-status'
import { useKeepFact, useNeedsYouByEntity, useNeedsYouDecision } from '@/hooks/use-needs-you'
import { useShellStore } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'

const ACTION_TEXT: Record<string, string> = {
  add: 'adds this',
  change: 'changes this',
  conflict: 'changes what a person set',
  bind: 'asks whether this is the same element as one of its own',
  relink: 'asks which of its facts this is',
}

/**
 * What the Sources say about one fact that a person decides: "not seen in latest scan", with
 * Remove and Keep, and each item of "Needs you" about it, with Accept and Reject. A scan acts on
 * the main model, so a draft shows none of it.
 */
export function FactImportState({ projectId, entityKind, entityId, stale, readOnly = false, onRemove }: {
  projectId: string
  entityKind: 'element' | 'relationship'
  entityId: string
  stale: StaleMark | null | undefined
  readOnly?: boolean
  /** Removes the fact from the model when no Source proposes its removal. */
  onRemove?: () => void
}) {
  const onDraft = useDraftStore((s) => s.activeDraftStreamId !== null)
  const items = useNeedsYouByEntity(projectId).get(entityId) ?? []
  const decide = useNeedsYouDecision(projectId)
  const keep = useKeepFact(projectId)
  if (onDraft || (!stale && items.length === 0)) return null

  const removal = items.find((i) => i.proposal.action === 'remove')
  const others = items.filter((i) => i.proposal.action !== 'remove')
  const busy = decide.isPending || keep.isPending
  const failures = decide.data?.failed ?? []
  const accept = (item: (typeof items)[number], acknowledgeImpact = false) =>
    decide.mutate({ scanId: item.source.scanId, decision: { accept: [...withDependencies(new Set([item.proposal.id]), item.source.items)], acknowledgeImpact } })
  const acknowledge = (result: DecisionResult | undefined) => result?.failed.some((f) => f.reason.includes('acknowledgeImpact')) ?? false

  return (
    <div className="mt-2 space-y-2">
      {stale && (
        <div className="rounded-md border border-dashed border-[var(--trace-retry)] px-2.5 py-2 text-xs">
          <p className="font-medium text-[var(--trace-retry)]">Not seen in latest scan</p>
          <p className="mt-0.5 text-muted-foreground">
            The Snapshot <span className="font-mono">{stale.sinceRevision.slice(0, 10)}</span> of {removal?.source.sourceName ?? 'its Source'} does not hold this. A scan deletes nothing.
          </p>
          {!readOnly && (
            <div className="mt-1.5 flex gap-1.5">
              <Button
                size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy}
                title="Take it out of the model"
                onClick={() => (removal ? accept(removal) : onRemove?.())}
              >
                Remove
              </Button>
              <Button
                size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={busy}
                title="Keep it. The mark goes until a later Snapshot changes the fact."
                onClick={() => keep.mutate({ entityKind, entityId })}
              >
                Keep
              </Button>
            </div>
          )}
        </div>
      )}
      {others.map((item) => {
        const change = item.proposal.changes.find((c) => !c.field.startsWith('observed.'))
        const decidable = !readOnly && item.proposal.canDecide && ['add', 'change', 'conflict'].includes(item.proposal.action)
        return (
          <div key={item.proposal.id} className="rounded-md border border-[var(--trace-event)]/60 px-2.5 py-2 text-xs">
            <p className="font-medium text-[var(--trace-event)]">Needs you</p>
            <p className="mt-0.5 text-muted-foreground">
              {item.source.sourceName} {ACTION_TEXT[item.proposal.action] ?? 'proposes a change'}
              {change && <>: <span className="font-mono text-foreground">{change.field}</span> {String(change.from ?? 'empty')} → <span className="text-foreground">{String(change.to ?? 'empty')}</span></>}
            </p>
            <div className="mt-1.5 flex gap-1.5">
              {decidable ? (
                <>
                  <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy} onClick={() => accept(item)}>Accept</Button>
                  <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={busy}
                    onClick={() => decide.mutate({ scanId: item.source.scanId, decision: { reject: [item.proposal.id] } })}>
                    Reject
                  </Button>
                </>
              ) : (
                <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => useShellStore.getState().openFlyout('import')}>
                  Open in Sources
                </Button>
              )}
            </div>
          </div>
        )
      })}
      {(decide.isError || keep.isError) && <p role="alert" className="text-xs text-destructive">{(decide.error ?? keep.error)!.message}</p>}
      {failures.length > 0 && (
        <div role="alert" className="space-y-1 text-xs">
          <ul className="space-y-0.5 text-muted-foreground">{failures.slice(0, 3).map((f) => <li key={f.id}>{f.reason}</li>)}</ul>
          {acknowledge(decide.data) && decide.variables && (
            <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy}
              onClick={() => decide.mutate({ ...decide.variables!, decision: { ...decide.variables!.decision, acknowledgeImpact: true } })}>
              I accept what this breaks
            </Button>
          )}
        </div>
      )}
    </div>
  )
}
