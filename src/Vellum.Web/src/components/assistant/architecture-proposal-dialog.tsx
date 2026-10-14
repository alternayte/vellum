import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { shownInline } from '@/lib/mutation-errors'
import { usePersonName } from '@/hooks/use-person-name'
import { OpenQuestions } from './open-questions'
import { describeOrigin, designApi, type ArchitectureItem, type ArchitectureProposal } from '@/lib/design-api'

interface Props {
  projectId: string
  proposal: ArchitectureProposal
  labels: Record<string, string>
  onClose: () => void
}

function withDependencies(selected: Set<string>, items: ArchitectureItem[]): Set<string> {
  const byId = new Map(items.map((item) => [item.id, item]))
  const result = new Set(selected)
  const queue = [...selected]
  while (queue.length > 0) {
    for (const dependency of byId.get(queue.pop()!)?.dependsOn ?? []) {
      if (!result.has(dependency) && byId.get(dependency)?.status === 'pending') {
        result.add(dependency)
        queue.push(dependency)
      }
    }
  }
  return result
}

/**
 * Additions the assistant proposes for the model. Each accepted item is written with the
 * label "inferred", the assistant's confidence and your account. It never changes or
 * removes what is in the model.
 */
export function ArchitectureProposalDialog({ projectId, proposal: initial, labels, onClose }: Props) {
  const queryClient = useQueryClient()
  const personOf = usePersonName(projectId)
  const [proposal, setProposal] = useState(initial)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const names = useMemo(() => ({ ...labels, ...Object.fromEntries(proposal.items.map((item) => [item.id, item.name])) }), [labels, proposal.items])
  const pending = proposal.items.filter((item) => item.status === 'pending')
  const accepting = withDependencies(selected, proposal.items)

  const decide = useMutation({
    mutationFn: ({ accept, reject }: { accept: string[]; reject: string[] }) => designApi.decideArchitecture(projectId, proposal.id, accept, reject),
    onSuccess: (next) => {
      setProposal(next)
      setSelected(new Set())
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
    },
    onError: shownInline,
  })

  const toggle = (id: string) => setSelected((current) => {
    const next = new Set(current)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    return next
  })
  const detail = (item: ArchitectureItem): string => {
    const p = item.proposed
    const name = (id: unknown) => (typeof id === 'string' ? (names[id] ?? 'an unknown part') : null)
    switch (item.entityKind) {
      case 'element': return [p.kind, p.technology, name(p.parentId) && `inside ${name(p.parentId)}`].filter(Boolean).join(' · ')
      case 'relationship': return `${name(p.fromId)} → ${name(p.toId)}${p.label ? ` · ${String(p.label)}` : ''}`
      case 'operation': return `${String(p.transport)} · offered by ${name(p.ownerId)}`
      case 'channel': return String(p.kind)
      case 'subscription': return `${name(p.consumerId)} on ${name(p.channelId)}`
      case 'binding': return `${name(p.fromId)} routes to ${name(p.toId)} · ${p.label ? `routing key ${String(p.label)}` : 'every message'}`
      case 'message': return `produced by ${name(p.producerId)}`
    }
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Proposed additions</DialogTitle>
          <DialogDescription>
            {describeOrigin(proposal)} Accepted items are added as inferred facts. Nothing in the model is changed or removed.
          </DialogDescription>
        </DialogHeader>

        {proposal.summary && <p className="text-sm text-foreground">{proposal.summary}</p>}

        <div className="min-h-0 flex-1 overflow-y-auto rounded-lg border border-border">
          {proposal.items.length === 0 && <p className="p-4 text-sm text-muted-foreground">The proposal holds no addition that can be made.</p>}
          <ul>
            {proposal.items.map((item) => (
              <li key={item.id} className="flex items-start gap-2.5 border-b border-border px-3 py-2 last:border-b-0">
                <input
                  type="checkbox"
                  className="mt-1 h-3.5 w-3.5 accent-[var(--primary)]"
                  checked={accepting.has(item.id)}
                  disabled={item.status !== 'pending' || (accepting.has(item.id) && !selected.has(item.id))}
                  onChange={() => toggle(item.id)}
                  aria-label={`Select ${item.entityKind} ${item.name}`}
                />
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-baseline gap-x-2">
                    <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{item.entityKind}</span>
                    <span className={item.status === 'pending' ? 'text-sm text-foreground' : 'text-sm text-muted-foreground'}>{item.name}</span>
                    {item.status !== 'pending' && <span className="rounded-full border border-border px-1.5 text-[10px] text-muted-foreground">{item.status}</span>}
                    <span className="ml-auto shrink-0 font-mono text-[10px] text-[var(--trace-retry)]" title="How sure the author of the proposal says it is. This is not a measurement.">
                      inferred · {Math.round(item.confidence * 100)}%
                    </span>
                  </p>
                  <p className="font-mono text-[11px] text-muted-foreground">{detail(item)}</p>
                  {item.rationale && <p className="mt-0.5 text-xs text-muted-foreground">{item.rationale}</p>}
                  {item.status !== 'pending' && item.decidedBy && (
                    <p className="mt-0.5 font-mono text-[10px] text-muted-foreground">
                      {item.status} by {personOf(item.decidedBy)}{item.decidedAt ? ` · ${new Date(item.decidedAt).toLocaleString()}` : ''}
                    </p>
                  )}
                  {proposal.itemIssues.filter((issue) => issue.itemId === item.id).map((issue, i) => (
                    <p key={i} className="mt-0.5 text-xs text-[var(--trace-retry)]">Cannot be added now: {issue.reason}</p>
                  ))}
                </div>
              </li>
            ))}
          </ul>
        </div>

        {proposal.resolvedToExisting.length > 0 && (
          <p className="text-xs text-muted-foreground">
            The model already has {proposal.resolvedToExisting.map((r) => r.name).join(', ')}. Vellum did not propose {proposal.resolvedToExisting.length === 1 ? 'it' : 'them'} again.
          </p>
        )}
        {proposal.omittedPrivate && proposal.omittedPrivate.count > 0 && (
          <p role="status" className="text-xs text-muted-foreground">
            The assistant did not see {proposal.omittedPrivate.count} {proposal.omittedPrivate.count === 1 ? 'part' : 'parts'} of the model that came from a private repository.
          </p>
        )}
        {proposal.rejectedReferences.length > 0 && proposal.origin !== 'ai' && (
          <ul className="max-h-24 space-y-0.5 overflow-y-auto font-mono text-[11px] text-muted-foreground">
            {proposal.rejectedReferences.map((r, i) => <li key={i}>{r.where}: {r.reason}</li>)}
          </ul>
        )}
        {proposal.rejectedReferences.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Vellum left out {proposal.rejectedReferences.length} {proposal.rejectedReferences.length === 1 ? 'part' : 'parts'} of the proposal that referred to things that do not exist, or that would change the model.
          </p>
        )}
        {proposal.openQuestions.length > 0 && (
          <details className="text-xs text-muted-foreground">
            <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">Questions {proposal.origin === 'ai' ? 'the assistant' : 'the agent'} could not answer ({proposal.openQuestions.length})</summary>
            <OpenQuestions projectId={projectId} proposalId={proposal.id} questions={proposal.openQuestions} />
          </details>
        )}
        {decide.isError && <p role="alert" className="text-xs text-destructive">{decide.error.message}</p>}

        <div className="flex flex-wrap items-center gap-2">
          <p className="min-w-0 flex-1 text-xs text-muted-foreground" aria-live="polite">
            {pending.length === 0 ? 'Every item is decided.' : selected.size === 0 ? `${pending.length} to decide.` : `${selected.size} selected.${accepting.size > selected.size ? ` Accepting also takes ${accepting.size - selected.size} that they need.` : ''}`}
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button variant="secondary" size="sm" disabled={selected.size === 0 || decide.isPending} onClick={() => decide.mutate({ accept: [], reject: [...selected] })}>Reject selected</Button>
          <Button size="sm" disabled={selected.size === 0 || decide.isPending} onClick={() => decide.mutate({ accept: [...accepting], reject: [] })}>
            {decide.isPending ? 'Adding…' : `Accept ${accepting.size || ''}`.trim()}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
