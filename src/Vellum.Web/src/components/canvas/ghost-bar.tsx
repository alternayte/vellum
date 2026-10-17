import { useMemo, useState } from 'react'
import { Panel } from '@xyflow/react'
import { ChevronDown } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { OpenQuestions } from '@/components/assistant/open-questions'
import { describeOrigin, type ArchitectureProposal } from '@/lib/design-api'
import { ghostItemData } from '@/lib/ghost-layer'
import { cn } from '@/lib/utils'
import { acceptableRest, useGhostDecisions } from '@/hooks/use-ghost-layer'
import { useCanvasStore } from '@/stores/canvas-store'
import { useDraftStore } from '@/stores/draft-store'
import { useGhostStore } from '@/stores/ghost-store'
import { useShellStore } from '@/stores/shell-store'
import { GhostActions } from './nodes/ghost-node'

interface Props {
  proposal: ArchitectureProposal
  /** Pending items that are not drawn on this level: other levels, and kinds the canvas does not draw as cards. */
  offCanvas: string[]
  /** How many ghosts this level shows. */
  shownOnLevel: number
  currentRootId: string | null
  canDecide: boolean
}

const linkClass = 'rounded text-muted-foreground underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary'

/**
 * The bar of the ghost layer: how many items wait, the decisions on all of them, the reasons
 * of a refused decision, and what the proposal says about itself.
 */
export function GhostBar({ proposal, offCanvas, shownOnLevel, currentRootId, canDecide }: Props) {
  const refusal = useGhostStore((s) => s.refusal)
  const error = useGhostStore((s) => s.error)
  const busy = useGhostStore((s) => s.busy)
  const close = useGhostStore((s) => s.close)
  const projectId = useGhostStore((s) => s.projectId)
  const selectGhost = useShellStore((s) => s.selectGhost)
  const branchStreamId = useDraftStore((s) => s.activeDraftStreamId)
  const decisions = useGhostDecisions()
  const [details, setDetails] = useState(false)
  const [others, setOthers] = useState(false)
  const pending = proposal.items.filter((item) => item.status === 'pending')
  const byId = useMemo(() => new Map(proposal.items.map((item) => [item.id, item])), [proposal.items])
  const dataOf = useMemo(() => ghostItemData(proposal, refusal?.reasons ?? null, canDecide), [proposal, refusal, canDecide])
  const otherBranch = (proposal.branchId ?? null) !== (branchStreamId ?? null)
  const accepted = proposal.items.filter((item) => item.status === 'accepted').length
  const rejected = proposal.items.filter((item) => item.status === 'rejected').length

  const rejectAll = () => {
    if (window.confirm(`Reject all ${pending.length} pending ${pending.length === 1 ? 'item' : 'items'}? A decided item does not change.`)) void decisions.rejectAll()
  }

  return (
    <Panel
      position="top-center"
      aria-label="Proposed additions"
      className="vellum-ghost-bar nowheel w-[42rem] max-w-[calc(100%-1.5rem)] rounded-lg border border-dashed border-[var(--trace-retry)]/60 bg-card/95 px-3 py-2 text-xs shadow-md backdrop-blur"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <p className="min-w-0 flex-1 basis-full sm:basis-0" aria-live="polite">
          <span className="font-medium text-foreground">Proposed additions</span>
          <span className="ml-2 text-muted-foreground">
            {pending.length === 0
              ? `Every item is decided: ${accepted} accepted, ${rejected} rejected.`
              : `${pending.length} pending${accepted + rejected > 0 ? ` · ${accepted} accepted, ${rejected} rejected` : ''}`}
          </span>
        </p>
        {canDecide && pending.length > 0 && (
          <>
            <Button size="sm" className="h-7 text-xs" disabled={busy} onClick={() => void decisions.acceptAll()}
              title="Accept every pending item in one decision. If one is refused, nothing is applied and the reasons show on the cards.">
              {busy ? 'Saving…' : `Accept all (${pending.length})`}
            </Button>
            <Button size="sm" variant="secondary" className="h-7 text-xs" disabled={busy} onClick={rejectAll}>Reject all</Button>
          </>
        )}
        <Button size="sm" variant="ghost" className="h-7 text-xs" onClick={close}
          title="Hide the layer. The proposal stays undecided under Review, Assistant.">
          Close
        </Button>
      </div>

      <div className="mt-1 flex items-start gap-2 text-muted-foreground">
        {/* The summary is text that the assistant or an agent wrote. It is shown as text. */}
        <p className={cn('min-w-0 flex-1', !details && 'truncate')}>{proposal.summary || proposal.request}</p>
        <button type="button" className={cn(linkClass, 'flex shrink-0 items-center gap-0.5')} aria-expanded={details} onClick={() => setDetails((v) => !v)}>
          Details <ChevronDown className={cn('h-3 w-3 transition-transform', details && 'rotate-180')} aria-hidden="true" />
        </button>
      </div>

      {details && (
        <div className="mt-1.5 max-h-56 space-y-1.5 overflow-y-auto border-t border-border pt-1.5 text-muted-foreground">
          <p>
            {describeOrigin(proposal)} Accepted items are added as inferred facts. Nothing in the model is changed or removed.
            {proposal.document && ` Read from ${proposal.document.path}; each item cites the lines it comes from.`}
          </p>
          {proposal.stale && <p>The model changed after the proposal was made. The problems shown are checked against the model as it is now.</p>}
          {proposal.resolvedToExisting.length > 0 && (
            <p>The model already has {proposal.resolvedToExisting.map((r) => r.name).join(', ')}. Vellum did not propose {proposal.resolvedToExisting.length === 1 ? 'it' : 'them'} again.</p>
          )}
          {proposal.omittedPrivate && proposal.omittedPrivate.count > 0 && (
            <p>The assistant did not see {proposal.omittedPrivate.count} {proposal.omittedPrivate.count === 1 ? 'part' : 'parts'} of the model that came from a private repository.</p>
          )}
          {proposal.rejectedReferences.length > 0 && (
            <>
              <p>Vellum left out {proposal.rejectedReferences.length} {proposal.rejectedReferences.length === 1 ? 'part' : 'parts'} of the proposal that referred to things that do not exist, or that would change the model.</p>
              {proposal.origin !== 'ai' && (
                <ul className="space-y-0.5 font-mono text-[11px]">
                  {proposal.rejectedReferences.map((r, i) => <li key={i}>{r.where}: {r.reason}</li>)}
                </ul>
              )}
            </>
          )}
          {proposal.openQuestions.length > 0 && projectId && (
            <div>
              <p className="text-foreground">Questions {proposal.origin === 'ai' ? 'the assistant' : 'the agent'} could not answer</p>
              <OpenQuestions projectId={projectId} proposalId={proposal.id} questions={proposal.openQuestions} />
            </div>
          )}
        </div>
      )}

      {otherBranch && (
        <p className="mt-1 text-[var(--trace-retry)]">
          The proposal adds to {proposal.branchId ? 'a draft or variant' : 'the main model'}, and the canvas shows another model. Accepted items go to {proposal.branchId ? 'that draft or variant' : 'the main model'}.
        </p>
      )}

      {pending.length > 0 && shownOnLevel === 0 && (
        <p className="mt-1 text-muted-foreground">
          No pending item is on this level of the canvas.
          {currentRootId && (
            <> <button type="button" className={linkClass} onClick={() => useCanvasStore.getState().drillTo(-1)}>Show the top level</button></>
          )}
        </p>
      )}

      {offCanvas.length > 0 && (
        <div className="mt-1">
          <button type="button" className={cn(linkClass, 'flex items-center gap-0.5')} aria-expanded={others} onClick={() => setOthers((v) => !v)}>
            {offCanvas.length} {offCanvas.length === 1 ? 'item is' : 'items are'} not drawn on this level <ChevronDown className={cn('h-3 w-3 transition-transform', others && 'rotate-180')} aria-hidden="true" />
          </button>
          {others && (
            <ul className="mt-1 max-h-40 space-y-0.5 overflow-y-auto">
              {offCanvas.map((id) => {
                const item = byId.get(id)
                if (!item) return null
                const data = dataOf(id)
                return (
                  <li key={id} className="group/ghost flex items-center gap-2 rounded px-1 py-0.5 hover:bg-muted">
                    <button type="button" className="flex min-w-0 flex-1 items-baseline gap-2 rounded text-left focus-visible:outline-2 focus-visible:outline-primary" onClick={() => selectGhost(id)}>
                      <span className="shrink-0 font-mono text-[10px] text-[var(--trace-retry)]">{item.alias}</span>
                      <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{item.entityKind}</span>
                      <span className="min-w-0 truncate text-foreground">{item.name}</span>
                      {data.refused.length > 0 && <span className="shrink-0 text-destructive">refused</span>}
                    </button>
                    <GhostActions data={data} />
                  </li>
                )
              })}
            </ul>
          )}
        </div>
      )}

      {error && !refusal && <p role="alert" className="mt-1 text-destructive">{error}</p>}
      {refusal && (
        <div role="alert" className="mt-1.5 space-y-1.5 rounded-md border border-destructive/40 px-2.5 py-1.5">
          <p className="text-foreground">
            Nothing was applied. {refusal.reasons.size} {refusal.reasons.size === 1 ? 'item cannot' : 'items cannot'} be accepted. The reasons show on {refusal.reasons.size === 1 ? 'its card' : 'their cards'}:
            {' '}{[...refusal.reasons.keys()].map((id) => byId.get(id)?.alias ?? id).join(', ')}.
          </p>
          {canDecide && (
            <div className="flex flex-wrap items-center gap-1.5">
              <Button size="sm" variant="secondary" className="h-7 text-xs" disabled={busy || acceptableRest(refusal, proposal.items).length === 0}
                onClick={() => void decisions.acceptRest(refusal)}
                title="Deselect the refused items and the items that need them, and accept the rest of the request. The refused items stay pending.">
                Deselect refused, accept the rest
              </Button>
              <Button size="sm" variant="secondary" className="h-7 text-xs" disabled={busy} onClick={() => void decisions.rejectRefused(refusal)}
                title="Reject the refused items and the items that need them. The rest stays pending.">
                Reject refused items
              </Button>
              <button type="button" className={linkClass} onClick={() => useGhostStore.getState().setRefusal(null)}>Dismiss</button>
            </div>
          )}
        </div>
      )}
    </Panel>
  )
}
