import { useState } from 'react'
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { FlowProposalDialog } from '@/components/assistant/flow-proposal-dialog'
import { useGhostStore } from '@/stores/ghost-store'
import { cn } from '@/lib/utils'
import { describeOrigin, designApi, mergeProposalPages, type ArchitectureProposal, type FlowProposal, type PendingProposal, type ProposalKind, type ProposalSummary } from '@/lib/design-api'
import { shownInline } from '@/lib/mutation-errors'
import { usePersonName } from '@/hooks/use-person-name'
import { useRunScenario } from '@/hooks/use-scenarios'
import { useShellStore } from '@/stores/shell-store'
import { useTraceStore } from '@/stores/trace-store'

interface Props {
  projectId: string
  branchId?: string | null
  labels: Record<string, string>
}

type Loaded = Record<ProposalKind, { items: ProposalSummary[]; cursor: string | null }>

/** Proposals of both kinds for a filter, 20 of each kind at a time. */
function useProposals(projectId: string, name: string, query: string) {
  const list = useInfiniteQuery({
    queryKey: ['projects', projectId, 'proposals', name],
    queryFn: ({ pageParam }) => designApi.proposals(projectId, query, pageParam),
    initialPageParam: undefined as Loaded | undefined,
    // Each page holds everything loaded so far. The next one goes on from it while a kind has more.
    getNextPageParam: (last) => (last.flow.cursor || last.architecture.cursor ? last : undefined),
  })
  const last = list.data?.pages.at(-1)
  return { list, merged: last ? mergeProposalPages(last) : null }
}

/** What was decided, in words. */
function decision(item: PendingProposal): string {
  if (item.kind === 'flow') return item.status === 'accepted' ? 'Accepted as a scenario' : 'Rejected'
  const accepted = item.acceptedItems ?? 0
  const rejected = item.rejectedItems ?? 0
  return accepted === 0 ? `All ${rejected} rejected` : rejected === 0 ? `All ${accepted} accepted` : `${accepted} accepted, ${rejected} rejected`
}

const rowClass = 'w-full rounded-md border border-border px-2.5 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary disabled:opacity-60'

/**
 * The proposals that the assistant made for you and that nobody decided, and the record of
 * decided proposals. A proposal costs tokens, so closing it does not lose it; and a decision
 * stays with who made it and when. Additions open as the ghost layer on the canvas; a decided
 * one opens here as its record.
 */
export function ProposalLists({ projectId, branchId, labels }: Props) {
  const queryClient = useQueryClient()
  const [flow, setFlow] = useState<FlowProposal | null>(null)
  const [record, setRecord] = useState<string | null>(null)
  const yours = useProposals(projectId, 'mine', 'mine=true&submitted=false&decided=false')
  const history = useProposals(projectId, 'history', 'decided=true')
  const who = usePersonName(projectId)
  const runScenario = useRunScenario(projectId, branchId)
  const closeFlyout = useShellStore((s) => s.closeFlyout)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'proposals'] })
  const open = useMutation({
    mutationFn: (item: PendingProposal): Promise<FlowProposal | ArchitectureProposal> =>
      item.kind === 'flow' ? designApi.getFlow(projectId, item.id) : designApi.getArchitecture(projectId, item.id),
    onSuccess: (result) => { if ('scenario' in result) setFlow(result); else useGhostStore.getState().open(projectId, result) },
    onError: shownInline,
  })
  const discard = useMutation({
    mutationFn: (item: PendingProposal) => designApi.discard(projectId, item.kind, item.id),
    onSuccess: refresh,
    onError: shownInline,
  })
  const failure = open.error ?? discard.error

  const when = (at: string) => new Date(at).toLocaleString()
  const kindLabel = (item: PendingProposal) => <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{item.kind === 'flow' ? 'flow' : 'model'}</span>
  const more = (list: ReturnType<typeof useProposals>['list']) => list.hasNextPage && (
    <Button variant="ghost" size="sm" className="mt-1 h-6 text-xs" disabled={list.isFetchingNextPage} onClick={() => list.fetchNextPage()}>
      {list.isFetchingNextPage ? 'Loading…' : 'Show more'}
    </Button>
  )

  return (
    <>
      <section aria-labelledby="review-your-proposals">
        <h3 id="review-your-proposals" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Your proposals</h3>
        <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
          Flows and additions the assistant proposed for you and that nobody decided. Open one to accept or reject it. Discard deletes it.
        </p>
        {yours.list.isError && <p role="alert" className="mt-1 text-xs text-destructive">{yours.list.error.message}</p>}
        {yours.merged?.items.length === 0 && <p className="mt-1.5 text-[11px] text-muted-foreground">No proposal waits for you.</p>}
        <ul className="mt-1.5 space-y-1">
          {yours.merged?.items.map((item) => {
            // Additions with a decided item are a record of that decision: they cannot be discarded.
            const partly = (item.acceptedItems ?? 0) + (item.rejectedItems ?? 0) > 0
            return (
              <li key={item.id} className="flex items-start gap-1">
                <button type="button" className={rowClass} disabled={open.isPending} onClick={() => open.mutate(item)}>
                  <p className="flex items-baseline gap-2">
                    {kindLabel(item)}
                    <span className="min-w-0 flex-1 truncate text-xs text-foreground">{item.name || item.request}</span>
                  </p>
                  <p className="mt-0.5 font-mono text-[10px] text-muted-foreground">
                    {when(item.createdAt)}
                    {partly ? ` · ${item.acceptedItems} accepted, ${item.rejectedItems} rejected, ${(item.items ?? 0) - (item.acceptedItems ?? 0) - (item.rejectedItems ?? 0)} to decide` : ''}
                  </p>
                </button>
                <Button
                  variant="ghost" size="sm" className="h-7 w-7 shrink-0 p-0 text-muted-foreground" aria-label={`Discard ${item.name || 'the proposal'}`}
                  title={partly ? 'Some items are decided. Reject the rest to close the proposal.' : 'Discard the proposal'}
                  disabled={partly || discard.isPending}
                  onClick={() => { if (window.confirm(`Discard “${item.name || item.request}”? This cannot be undone.`)) discard.mutate(item) }}
                >
                  <Trash2 className="h-3 w-3" />
                </Button>
              </li>
            )
          })}
        </ul>
        {more(yours.list)}
      </section>

      <section aria-labelledby="review-proposal-history">
        <h3 id="review-proposal-history" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">History</h3>
        <p className="mt-1 text-xs leading-relaxed text-muted-foreground">Every decided proposal of this project, from the assistant or from an agent, with who decided it and when.</p>
        {history.list.isError && <p role="alert" className="mt-1 text-xs text-destructive">{history.list.error.message}</p>}
        {history.merged?.items.length === 0 && <p className="mt-1.5 text-[11px] text-muted-foreground">No proposal is decided yet.</p>}
        <ul className="mt-1.5 space-y-1">
          {history.merged?.items.map((item) => (
            <li key={item.id}>
              <button
                type="button" className={rowClass} disabled={open.isPending}
                aria-expanded={item.kind === 'architecture' ? record === item.id : undefined}
                onClick={() => (item.kind === 'architecture' ? setRecord(record === item.id ? null : item.id) : open.mutate(item))}
              >
                <p className="flex items-baseline gap-2">
                  {kindLabel(item)}
                  <span className="min-w-0 flex-1 truncate text-xs text-foreground">{item.name || item.request}</span>
                  <span className={item.status === 'rejected' ? 'shrink-0 text-[11px] text-muted-foreground' : 'shrink-0 text-[11px] text-[var(--trace-request)]'}>{decision(item)}</span>
                </p>
                <p className="mt-0.5 font-mono text-[10px] text-muted-foreground">
                  {item.decidedBy ? who(item.decidedBy) : 'unknown'} · {item.decidedAt ? when(item.decidedAt) : 'unknown time'}
                  {item.origin !== 'ai' ? ' · submitted by an agent' : ''}
                  {item.kind === 'architecture' && <ChevronDown className={cn('ml-1 inline h-3 w-3 transition-transform', record === item.id && 'rotate-180')} aria-hidden="true" />}
                </p>
              </button>
              {record === item.id && <AdditionsRecord projectId={projectId} proposalId={item.id} />}
            </li>
          ))}
        </ul>
        {more(history.list)}
      </section>
      {failure && <p role="alert" className="text-xs text-destructive">{failure.message}</p>}

      {flow && (
        <FlowProposalDialog
          projectId={projectId} proposal={flow} labels={labels}
          onClose={() => { setFlow(null); void refresh() }}
          onAccepted={(scenarioId) => {
            setFlow(null)
            void refresh()
            closeFlyout()
            runScenario.mutate(scenarioId, { onSuccess: (run) => useTraceStore.getState().load(run, scenarioId) })
          }}
        />
      )}
    </>
  )
}

/** The record of decided additions: each item, its decision, who made it and when. */
function AdditionsRecord({ projectId, proposalId }: { projectId: string; proposalId: string }) {
  const who = usePersonName(projectId)
  const proposal = useQuery({ queryKey: ['projects', projectId, 'proposals', 'record', proposalId], queryFn: () => designApi.getArchitecture(projectId, proposalId) })
  if (proposal.isError) return <p role="alert" className="mt-1 px-2.5 text-xs text-destructive">{proposal.error.message}</p>
  if (!proposal.data) return <p className="mt-1 px-2.5 text-[11px] text-muted-foreground">Loading…</p>
  return (
    <div className="mt-1 space-y-1 rounded-md border border-border px-2.5 py-1.5 text-[11px]">
      {/* Text that the assistant or an agent wrote. It is shown as text. */}
      {proposal.data.summary && <p className="text-muted-foreground">{proposal.data.summary}</p>}
      <p className="text-muted-foreground">{describeOrigin(proposal.data)}</p>
      <ul className="space-y-1">
        {proposal.data.items.map((i) => (
          <li key={i.id}>
            <p className="flex items-baseline gap-2">
              <span className="shrink-0 font-mono text-[10px] text-muted-foreground">{i.alias}</span>
              <span className="min-w-0 flex-1 truncate text-foreground">{i.name}</span>
              <span className={i.status === 'accepted' ? 'shrink-0 text-[var(--trace-request)]' : 'shrink-0 text-muted-foreground'}>{i.status}</span>
            </p>
            {i.decidedBy && (
              <p className="font-mono text-[10px] text-muted-foreground">
                {who(i.decidedBy)}{i.decidedAt ? ` · ${new Date(i.decidedAt).toLocaleString()}` : ''} · {i.entityKind} · {Math.round(i.confidence * 100)}%
              </p>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}
