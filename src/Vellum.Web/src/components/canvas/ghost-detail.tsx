import { Button } from '@/components/ui/button'
import { SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { describeOrigin, type ArchitectureItem, type ArchitectureProposal } from '@/lib/design-api'
import { ghostItemData } from '@/lib/ghost-layer'
import { useGhostDecisions } from '@/hooks/use-ghost-layer'
import { useGhostStore } from '@/stores/ghost-store'
import { useShellStore } from '@/stores/shell-store'
import { GhostProblems } from './nodes/ghost-node'

const SECTION = 'mb-1 text-xs text-muted-foreground'

/** One line of the values of an item, with names in place of IDs. */
function describeItem(item: ArchitectureItem, name: (id: unknown) => string | null): string {
  const p = item.proposed
  switch (item.entityKind) {
    case 'element': return [p.kind, p.technology, name(p.parentId) && `inside ${name(p.parentId)}`].filter(Boolean).join(' · ')
    case 'relationship': return `${name(p.fromId)} → ${name(p.toId)}${p.label ? ` · ${String(p.label)}` : ''}${p.technology ? ` · ${String(p.technology)}` : ''}`
    case 'operation': return `${String(p.transport)} · offered by ${name(p.ownerId)}`
    case 'channel': return String(p.kind)
    case 'subscription': return `${name(p.consumerId)} on ${name(p.channelId)}`
    case 'binding': return `${name(p.fromId)} routes to ${name(p.toId)} · ${p.label ? `routing key ${String(p.label)}` : 'every message'}`
    case 'message': return `produced by ${name(p.producerId)}`
  }
}

/**
 * The detail of the selected ghost: what the item adds, why, how sure its author says it is,
 * where it comes from, and its decision. Nothing of it is in the model yet.
 */
export function GhostDetail({ proposal, itemId, labels, canDecide }: {
  proposal: ArchitectureProposal
  itemId: string
  labels: Record<string, string>
  canDecide: boolean
}) {
  const refusal = useGhostStore((s) => s.refusal)
  const busy = useGhostStore((s) => s.busy)
  const selectGhost = useShellStore((s) => s.selectGhost)
  const { accept, reject } = useGhostDecisions()
  const item = proposal.items.find((i) => i.id === itemId)
  if (!item) return null
  const byId = new Map(proposal.items.map((i) => [i.id, i]))
  const name = (id: unknown) => (typeof id === 'string' ? (byId.get(id)?.name ?? labels[id] ?? 'an unknown part') : null)
  const data = ghostItemData(proposal, refusal?.reasons ?? null, canDecide)(itemId)
  const needs = item.dependsOn.map((id) => byId.get(id)).filter((d): d is ArchitectureItem => !!d)
  const neededBy = proposal.items.filter((d) => d.status === 'pending' && d.dependsOn.includes(itemId))
  const description = typeof item.proposed.description === 'string' ? item.proposed.description : null

  return (
    <div>
      <SheetHeader className="pr-12">
        <SheetTitle className="font-display text-base leading-tight">{item.name}</SheetTitle>
        <p className="font-mono text-[10px] uppercase tracking-wider text-[var(--trace-retry)]">
          proposed {item.entityKind === 'element' && typeof item.proposed.kind === 'string' ? item.proposed.kind : item.entityKind} · {item.alias}
        </p>
      </SheetHeader>
      <div className="mt-2 space-y-4 px-4 pb-4 text-sm">
        <p className="rounded-md border border-dashed border-[var(--trace-retry)]/60 px-2.5 py-1.5 text-xs text-muted-foreground">
          {item.status === 'pending'
            ? 'Not in the model. An accepted item is added as an inferred fact with this proposal as its source.'
            : `This item is ${item.status}.`}
        </p>

        {item.status === 'pending' && canDecide && (
          <div className="flex flex-wrap gap-2">
            <Button size="sm" className="flex-1" disabled={busy} onClick={() => void accept(itemId)}>
              {data.needs > 0 ? `Accept, with ${data.needs} ${data.needs === 1 ? 'item' : 'items'} it needs` : 'Accept'}
            </Button>
            <Button size="sm" variant="secondary" className="flex-1" disabled={busy} onClick={() => void reject(itemId)}>
              {data.neededBy > 0 ? `Reject, with ${data.neededBy} ${data.neededBy === 1 ? 'item that needs' : 'items that need'} it` : 'Reject'}
            </Button>
          </div>
        )}
        <GhostProblems data={data} />

        <section>
          <h3 className={SECTION}>Confidence</h3>
          <p className="font-mono text-xs text-[var(--trace-retry)]">inferred · {Math.round(item.confidence * 100)}%</p>
          <p className="mt-0.5 text-[11px] text-muted-foreground">How sure the author of the proposal says it is. This is not a measurement.</p>
        </section>

        {item.rationale && (
          <section>
            <h3 className={SECTION}>Rationale</h3>
            {/* Text that the assistant or an agent wrote. It is shown as text. */}
            <p className="whitespace-pre-wrap text-sm text-foreground">{item.rationale}</p>
          </section>
        )}

        <section>
          <h3 className={SECTION}>What it adds</h3>
          <p className="font-mono text-xs text-foreground">{describeItem(item, name)}</p>
          {description && <p className="mt-1 text-xs text-muted-foreground">{description}</p>}
        </section>

        <section>
          <h3 className={SECTION}>Alias</h3>
          <p className="font-mono text-xs text-foreground">{item.alias}</p>
        </section>

        <section>
          <h3 className={SECTION}>Citation</h3>
          {item.citation
            ? <p className="break-all font-mono text-[11px] text-foreground">{item.citation}</p>
            : <p className="text-xs text-muted-foreground">None. {describeOrigin(proposal)}</p>}
        </section>

        {needs.length > 0 && (
          <section>
            <h3 className={SECTION}>Needs</h3>
            <ul className="space-y-0.5 text-xs">
              {needs.map((d) => (
                <li key={d.id}>
                  <button type="button" className="rounded text-left text-primary hover:underline focus-visible:outline-2 focus-visible:outline-primary" onClick={() => selectGhost(d.id)}>
                    {d.alias} · {d.name}
                  </button>
                  <span className="ml-1 text-muted-foreground">{d.status}</span>
                </li>
              ))}
            </ul>
          </section>
        )}
        {neededBy.length > 0 && (
          <section>
            <h3 className={SECTION}>Needed by</h3>
            <ul className="space-y-0.5 text-xs">
              {neededBy.map((d) => (
                <li key={d.id}>
                  <button type="button" className="rounded text-left text-primary hover:underline focus-visible:outline-2 focus-visible:outline-primary" onClick={() => selectGhost(d.id)}>
                    {d.alias} · {d.name}
                  </button>
                </li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </div>
  )
}
