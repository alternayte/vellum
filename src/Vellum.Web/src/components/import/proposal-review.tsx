import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { describeNotCovered, importApi, withDependencies, type Proposal, type ProposalAction, type ProposalEvidence, type ScanSummary } from '@/lib/import-api'

const ACTION: Record<ProposalAction, { label: string; color: string; sign: string }> = {
  add: { label: 'Add', color: 'var(--trace-request)', sign: '+' },
  change: { label: 'Change', color: 'var(--trace-retry)', sign: '~' },
  remove: { label: 'Remove', color: 'var(--trace-failure)', sign: '−' },
  conflict: { label: 'Conflict', color: 'var(--trace-event)', sign: '!' },
  relink: { label: 'Match', color: 'var(--trace-data)', sign: '?' },
}

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'empty'
  return typeof value === 'string' ? value : JSON.stringify(value)
}

/**
 * The review of one scan: each proposal with what would change and the source lines that
 * support it. A conflict shows the value in the model and the value in the source; neither
 * wins until a person accepts or rejects it.
 */
export function ProposalReview({ projectId, scan, onClose }: { projectId: string; scan: ScanSummary; onClose: () => void }) {
  const queryClient = useQueryClient()
  const proposals = useQuery({
    queryKey: ['projects', projectId, 'imports', scan.id, 'proposals'],
    queryFn: () => importApi.proposals(projectId, scan.id),
  })
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [open, setOpen] = useState<string | null>(null)
  const [failed, setFailed] = useState<{ id: string; reason: string }[]>([])
  const [acknowledged, setAcknowledged] = useState(false)
  const [links, setLinks] = useState<Record<string, string | null>>({})

  const all = useMemo(() => proposals.data ?? [], [proposals.data])
  const pending = all.filter((p) => p.status === 'pending')
  const groups = useMemo(() => {
    const byKind = new Map<string, Proposal[]>()
    for (const proposal of all) byKind.set(proposal.entityKind, [...(byKind.get(proposal.entityKind) ?? []), proposal])
    return [...byKind.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [all])

  const decide = useMutation({
    mutationFn: ({ accept, reject }: { accept: string[]; reject: string[] }) =>
      importApi.decide(projectId, scan.id, {
        accept, reject, acknowledgeImpact: acknowledged,
        relink: Object.entries(links).map(([id, factKey]) => ({ id, factKey })),
      }),
    onSuccess: (result) => {
      setFailed(result.failed)
      setSelected(new Set(result.failed.map((f) => f.id)))
      if (result.failed.length === 0) { setLinks({}); setAcknowledged(false) }
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports', scan.id, 'proposals'] })
      // Accepted proposals changed the model: every model query is stale.
      queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
    },
    onError: shownInline,
  })

  const toggle = (id: string) =>
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  const accepting = withDependencies(selected, all)
  const added = accepting.size - selected.size
  // Accepting something that breaks a scenario or another part needs a deliberate tick.
  const breaking = all.filter((p) => accepting.has(p.id) && p.impact && (p.impact.scenarios.length > 0 || p.impact.entities.length > 0))
  const notCovered = describeNotCovered(scan.coverage?.linkedFacts)
  const linkCount = Object.keys(links).length

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>Review {scan.repository}</DialogTitle>
          <DialogDescription>
            Read at commit <span className="font-mono">{scan.commitSha?.slice(0, 10)}</span>. Each proposal states what the source shows at the cited lines, and nothing more.
          </DialogDescription>
        </DialogHeader>

        {!scan.canDecide && (
          <p role="status" className="rounded-md border border-border px-3 py-2 text-xs text-foreground">
            This is a comparison with <span className="font-mono">{scan.trackedRef || 'the tracked ref'}</span>, which the model follows. You can read it, and you cannot accept from it. {scan.canDecideReason}
          </p>
        )}
        {notCovered && <p role="status" className="rounded-md border border-border px-3 py-2 text-xs text-muted-foreground">{notCovered}</p>}
        {!scan.isComplete && (
          <p role="status" className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2 text-xs text-foreground">
            This scan is partial. A limit stopped it before every file was read, so it proposes no removals and missing items prove nothing.
          </p>
        )}

        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <span>{pending.length} pending of {all.length}</span>
          <div className="flex-1" />
          <button type="button" className="rounded underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary"
            onClick={() => setSelected(new Set(pending.filter((p) => p.action !== 'conflict' && p.action !== 'relink' && p.canDecide).map((p) => p.id)))}>
            Select all without conflicts
          </button>
          <button type="button" className="rounded underline-offset-2 hover:text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary" onClick={() => setSelected(new Set())}>
            Clear
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto rounded-lg border border-border">
          {proposals.isLoading && <p className="p-4 text-sm text-muted-foreground">Loading proposals…</p>}
          {proposals.isError && <p role="alert" className="p-4 text-sm text-destructive">{proposals.error.message}</p>}
          {proposals.data && all.length === 0 && (
            <p className="p-4 text-sm text-muted-foreground">The source matches the model. This scan proposes no change.</p>
          )}
          {groups.map(([kind, items]) => (
            <section key={kind} aria-label={kind}>
              <h3 className="sticky top-0 z-10 border-b border-border bg-card px-3 py-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
                {kind} <span className="font-mono">{items.length}</span>
              </h3>
              <ul>
                {items.map((proposal) => {
                  const action = ACTION[proposal.action]
                  const isPending = proposal.status === 'pending'
                  const problem = failed.find((f) => f.id === proposal.id)
                  return (
                    <li key={proposal.id} className="border-b border-border last:border-b-0">
                      <div className="flex items-start gap-2.5 px-3 py-2">
                        <input
                          type="checkbox"
                          className="mt-1 h-3.5 w-3.5 accent-[var(--primary)]"
                          checked={accepting.has(proposal.id)}
                          disabled={!isPending || !proposal.canDecide || proposal.action === 'relink' || (accepting.has(proposal.id) && !selected.has(proposal.id))}
                          onChange={() => toggle(proposal.id)}
                          aria-label={`Select ${action.label.toLowerCase()} ${proposal.name}`}
                        />
                        <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded border font-mono text-[11px]" style={{ borderColor: action.color, color: action.color }} aria-hidden="true">
                          {action.sign}
                        </span>
                        <div className="min-w-0 flex-1">
                          <button
                            type="button"
                            aria-expanded={open === proposal.id}
                            onClick={() => setOpen(open === proposal.id ? null : proposal.id)}
                            className="flex w-full items-baseline gap-2 rounded text-left focus-visible:outline-2 focus-visible:outline-primary"
                          >
                            <span className="text-xs font-medium" style={{ color: action.color }}>{action.label}</span>
                            <span className={cn('min-w-0 truncate text-sm', isPending ? 'text-foreground' : 'text-muted-foreground')}>{proposal.name}</span>
                            {!isPending && <span className="rounded-full border border-border px-1.5 text-[10px] text-muted-foreground">{proposal.status}</span>}
                            <span className="ml-auto shrink-0 font-mono text-[10px] text-muted-foreground">
                              {proposal.evidence[0] ? `${proposal.evidence[0].path}:${proposal.evidence[0].startLine}` : 'no source line'}
                            </span>
                          </button>
                          {problem && <p role="alert" className="mt-1 text-xs text-destructive">{problem.reason}</p>}
                          {open === proposal.id && (
                            <ProposalDetail
                              projectId={projectId} scanId={scan.id} proposal={proposal}
                              link={links[proposal.id]}
                              onLink={(factKey) => setLinks((current) => ({ ...current, [proposal.id]: factKey }))}
                            />
                          )}
                        </div>
                      </div>
                    </li>
                  )
                })}
              </ul>
            </section>
          ))}
        </div>

        {decide.isError && <p role="alert" className="text-xs text-destructive">{decide.error.message}</p>}

        {breaking.length > 0 && (
          <label className="flex items-start gap-2 rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs text-foreground">
            <input type="checkbox" className="mt-0.5 h-3.5 w-3.5 accent-[var(--primary)]" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
            <span>
              {breaking.length === 1 ? 'One selected proposal breaks' : `${breaking.length} selected proposals break`} something that exists:{' '}
              {[...new Set(breaking.flatMap((p) => [...p.impact!.scenarios.map((s) => `scenario “${s.name}”`), ...p.impact!.entities.map((e) => `${e.entityKind} ${e.name}`)]))].slice(0, 6).join(', ')}.
              I accept that.
            </span>
          </label>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <p className="min-w-0 flex-1 text-xs text-muted-foreground" aria-live="polite">
            {selected.size === 0
              ? linkCount > 0 ? `${linkCount} ${linkCount === 1 ? 'match' : 'matches'} chosen.` : 'Select proposals to accept or reject.'
              : `${selected.size} selected.${added > 0 ? ` Accepting also takes ${added} that ${added === 1 ? 'it depends' : 'they depend'} on.` : ''}`}
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button variant="secondary" size="sm" disabled={selected.size === 0 || decide.isPending} onClick={() => decide.mutate({ accept: [], reject: [...selected] })}>
            Reject selected
          </Button>
          <Button size="sm" disabled={(selected.size === 0 && linkCount === 0) || decide.isPending || !scan.canDecide || (breaking.length > 0 && !acknowledged)} onClick={() => decide.mutate({ accept: [...accepting], reject: [] })}>
            {decide.isPending ? 'Applying…' : selected.size === 0 ? 'Apply matches' : `Accept ${accepting.size || ''}`.trim()}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

/**
 * The proposed values as rows a person can read: what the source showed (`observed`) one
 * attribute per row, and a name in place of the ID it stands for.
 */
function readable(proposed: Record<string, unknown>): { field: string; from: unknown; to: unknown }[] {
  const rows: { field: string; from: unknown; to: unknown }[] = []
  for (const [field, value] of Object.entries(proposed)) {
    if (field.endsWith('Id') && `${field.slice(0, -2)}Name` in proposed) continue
    if (field === 'observed' && value && typeof value === 'object' && !Array.isArray(value)) {
      for (const [attribute, observed] of Object.entries(value)) rows.push({ field: attribute, from: undefined, to: observed })
    } else {
      rows.push({ field: field.endsWith('Name') && field !== 'name' ? field.slice(0, -4) : field, from: undefined, to: value })
    }
  }
  return rows
}

function ProposalDetail({ projectId, scanId, proposal: listed, link, onLink }: {
  projectId: string; scanId: string; proposal: Proposal
  link: string | null | undefined; onLink: (factKey: string | null) => void
}) {
  // The list cuts large bodies; the detail loads the whole proposal when it was cut.
  const whole = useQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'proposal', listed.id],
    queryFn: () => importApi.proposal(projectId, scanId, listed.id),
    enabled: listed.truncated,
    staleTime: Infinity,
  })
  const proposal = whole.data ?? listed
  if (proposal.action === 'relink') return <RelinkChoice proposal={proposal} link={link} onLink={onLink} />
  const fields = proposal.changes.length > 0 ? proposal.changes : readable(proposal.proposed ?? {})
  return (
    <div className="mt-2 space-y-3 pb-1">
      {fields.length > 0 && (
        <table className="w-full text-left text-xs">
          <thead className="text-[10px] uppercase tracking-wider text-muted-foreground">
            <tr>
              <th className="py-1 pr-3 font-medium">Field</th>
              {proposal.changes.length > 0 && <th className="py-1 pr-3 font-medium">In the model</th>}
              <th className="py-1 font-medium">In the source</th>
            </tr>
          </thead>
          <tbody>
            {fields.map((change) => (
              <tr key={change.field} className="border-t border-border align-top">
                <td className="py-1 pr-3 font-mono text-muted-foreground">{change.field}</td>
                {proposal.changes.length > 0 && <td className="break-all py-1 pr-3 text-muted-foreground">{show(change.from)}</td>}
                <td className="break-all py-1 text-foreground">{show(change.to)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {proposal.impact && (proposal.impact.scenarios.length > 0 || proposal.impact.entities.length > 0) && (
        <div className="rounded-md border border-[var(--trace-failure)]/60 px-2.5 py-2 text-xs">
          <p className="font-medium text-foreground">Accepting this breaks:</p>
          <ul className="mt-1 space-y-0.5 text-muted-foreground">
            {proposal.impact.scenarios.map((s) => <li key={s.id}>Scenario “{s.name}”, {s.stepIds.length === 1 ? 'step' : 'steps'} <span className="font-mono">{s.stepIds.join(', ')}</span></li>)}
            {proposal.impact.entities.map((e) => <li key={e.id}>{e.entityKind} {e.name}</li>)}
          </ul>
        </div>
      )}
      {!proposal.canDecide && proposal.canDecideReason && <p className="text-xs text-muted-foreground">{proposal.canDecideReason}</p>}
      {proposal.action === 'conflict' && (
        <p className="text-xs text-muted-foreground">
          A person set the value in the model. Accept to replace it with the value in the source. Reject to keep the model as it is.
        </p>
      )}
      {proposal.evidence.map((evidence) => (
        <SourceLines key={evidence.pointer} projectId={projectId} scanId={scanId} evidence={evidence} />
      ))}
    </div>
  )
}

function SourceLines({ projectId, scanId, evidence }: { projectId: string; scanId: string; evidence: ProposalEvidence }) {
  const source = useQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'source', evidence.path, evidence.startLine, evidence.endLine],
    queryFn: () => importApi.source(projectId, scanId, evidence),
    staleTime: Infinity,
  })
  return (
    <figure className="overflow-hidden rounded-md border border-border">
      <figcaption className="flex items-center gap-2 border-b border-border bg-muted/40 px-2 py-1 font-mono text-[10px] text-muted-foreground">
        <span className="min-w-0 flex-1 truncate">{evidence.path}:{evidence.startLine}-{evidence.endLine}</span>
        <span className="shrink-0">{evidence.rule}</span>
        <a href={evidence.url} target="_blank" rel="noreferrer noopener" className="flex shrink-0 items-center gap-1 rounded text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary">
          GitHub <ExternalLink className="h-3 w-3" />
        </a>
      </figcaption>
      {source.isError ? (
        <p className="px-2 py-1.5 text-xs text-muted-foreground">These lines are not stored. Open them on GitHub.</p>
      ) : (
        <pre tabIndex={0} className="max-h-48 overflow-auto bg-background px-2 py-1.5 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary">
          {source.data
            ? source.data.lines.map((line, i) => (
                <div key={i} className="flex gap-3">
                  <span className="w-8 shrink-0 select-none text-right text-muted-foreground">{source.data.startLine + i}</span>
                  {/* Repository text is shown as text. It is never rendered as markup. */}
                  <span className="whitespace-pre">{line}</span>
                </div>
              ))
            : 'Loading…'}
        </pre>
      )}
    </figure>
  )
}

/**
 * The extraction rules changed, and Vellum cannot tell by an exact rule which fact of this
 * scan is the thing the model already holds. A person chooses; until then nothing is
 * removed, added or marked stale for it.
 */
function RelinkChoice({ proposal, link, onLink }: { proposal: Proposal; link: string | null | undefined; onLink: (factKey: string | null) => void }) {
  const candidates = ((proposal.proposed as { candidates?: { factKey: string; name: string; evidence: ProposalEvidence[] }[] } | null)?.candidates) ?? []
  const name = `relink-${proposal.id}`
  return (
    <fieldset className="mt-2 space-y-1.5 pb-1 text-xs">
      <legend className="text-muted-foreground">Which fact of this scan is “{proposal.name}”?</legend>
      {candidates.map((candidate) => (
        <label key={candidate.factKey} className="flex items-start gap-2 rounded-md border border-border px-2.5 py-1.5">
          <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={link === candidate.factKey} onChange={() => onLink(candidate.factKey)} />
          <span className="min-w-0">
            <span className="text-foreground">{candidate.name}</span>
            {candidate.evidence[0] && <span className="block font-mono text-[10px] text-muted-foreground">{candidate.evidence[0].path}:{candidate.evidence[0].startLine}-{candidate.evidence[0].endLine}</span>}
          </span>
        </label>
      ))}
      <label className="flex items-start gap-2 rounded-md border border-border px-2.5 py-1.5">
        <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={link === null} onChange={() => onLink(null)} />
        <span className="text-foreground">None of these. The source no longer has it.</span>
      </label>
    </fieldset>
  )
}
