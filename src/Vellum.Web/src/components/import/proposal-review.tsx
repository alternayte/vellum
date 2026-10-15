import { useMemo, useState } from 'react'
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { CONTENT_NOT_HELD, describeFactReason, describeNotCovered, importApi, withDependencies, type Proposal, type ProposalAction, type ProposalEvidence, type ScanSummary } from '@/lib/import-api'
import { prettyJson } from '@/lib/pretty-json'
import { BindQuestion, SourceSides } from './source-facts'

const ACTION: Record<ProposalAction, { label: string; color: string; sign: string }> = {
  add: { label: 'Add', color: 'var(--trace-request)', sign: '+' },
  change: { label: 'Change', color: 'var(--trace-retry)', sign: '~' },
  remove: { label: 'Remove', color: 'var(--trace-failure)', sign: '−' },
  conflict: { label: 'Conflict', color: 'var(--trace-event)', sign: '!' },
  relink: { label: 'Match', color: 'var(--trace-data)', sign: '?' },
  bind: { label: 'Same as?', color: 'var(--trace-data)', sign: '=' },
}

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'empty'
  return typeof value === 'string' ? value : JSON.stringify(value)
}

/**
 * A value of a proposal. An object, or a text that holds a JSON object or array (a schema),
 * is indented in a scrolling block; anything else is a line of text.
 */
function Value({ value, muted }: { value: unknown; muted?: boolean }) {
  const pretty = prettyJson(value)
  if (pretty === null) return <span className={muted ? 'break-all text-muted-foreground' : 'break-all text-foreground'}>{show(value)}</span>
  return (
    // Repository text is shown as text. It is never rendered as markup.
    <pre
      tabIndex={0}
      className={`max-h-56 overflow-auto rounded-md border border-border bg-background px-2 py-1.5 font-mono text-[11px] leading-relaxed focus-visible:outline-2 focus-visible:outline-primary ${muted ? 'text-muted-foreground' : 'text-foreground'}`}
    >
      {pretty}
    </pre>
  )
}

/**
 * The review of one scan: each proposal with what would change and the source lines that
 * support it. A conflict shows the value in the model and the value in the source; neither
 * wins until a person accepts or rejects it.
 */
export function ProposalReview({ projectId, scan, sourceName, onClose }: {
  projectId: string
  scan: ScanSummary
  /** What people call the Source of the scan. Without it, the repository. */
  sourceName?: string
  onClose: () => void
}) {
  const source = sourceName ?? scan.repository
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
  const [answers, setAnswers] = useState<Record<string, boolean>>({})
  const [notice, setNotice] = useState<string | null>(null)

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
        bind: Object.entries(answers).map(([id, same]) => ({ id, same })),
      }),
    onSuccess: (result) => {
      setFailed(result.failed)
      setSelected(new Set(result.failed.map((f) => f.id)))
      if (result.failed.length === 0) { setLinks({}); setAnswers({}); setAcknowledged(false) }
      setNotice(result.answered.length === 0 ? null
        : result.replanned ? 'Answers applied. The proposals of this scan were planned again with them.'
          : 'Answers applied. The model changed while the scan was planned again: scan the Source again to see the new proposals.')
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
  const answerCount = Object.keys(answers).length

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>Review {scan.repository}</DialogTitle>
          <DialogDescription>
            {!scan.contentHeld
              ? <>Pushed by vellum scan as Snapshot <span className="font-mono">{scan.commitSha?.slice(0, 10)}</span>{scan.reportedCommitSha && <> from commit <span className="font-mono">{scan.reportedCommitSha.slice(0, 10)}</span> (as reported)</>}. </>
              // A runtime Source has no commit: its revision is the 64-character Snapshot ID.
              : <>{scan.commitSha?.length === 64 ? 'Read as Snapshot ' : 'Read at commit '}<span className="font-mono">{scan.commitSha?.slice(0, 10)}</span>. </>}
            Each proposal states what the source shows at the cited lines, and nothing more.
          </DialogDescription>
        </DialogHeader>

        {!scan.contentHeld && (
          <p role="status" className="rounded-md border border-border px-3 py-2 text-xs text-foreground">{CONTENT_NOT_HELD}</p>
        )}

        {!scan.canDecide && (
          <p role="status" className="rounded-md border border-border px-3 py-2 text-xs text-foreground">
            This is a comparison with <span className="font-mono">{scan.trackedRef || 'the tracked ref'}</span>, which the model follows. You can read it, and you cannot accept from it. To accept from this ref, close this review and select Follow this ref.
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
            onClick={() => setSelected(new Set(pending.filter((p) => p.action !== 'conflict' && p.action !== 'relink' && p.action !== 'bind' && p.canDecide).map((p) => p.id)))}>
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
                          disabled={!isPending || !proposal.canDecide || proposal.action === 'relink' || proposal.action === 'bind' || (accepting.has(proposal.id) && !selected.has(proposal.id))}
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
                            <span
                              className="shrink-0 rounded-full border border-border px-1.5 font-mono text-[10px] text-muted-foreground"
                              title={proposal.factStatus === 'declared' ? 'A model file states this. Accepted, it is a declared fact.' : 'The source code shows this. Accepted, it is an observed-in-source fact.'}
                            >
                              {proposal.factStatus}
                            </span>
                            <span className="ml-auto shrink-0 font-mono text-[10px] text-muted-foreground">
                              {proposal.evidence[0] ? `${proposal.evidence[0].path}:${proposal.evidence[0].startLine}` : 'no source line'}
                            </span>
                          </button>
                          {problem && <p role="alert" className="mt-1 text-xs text-destructive">{problem.reason}</p>}
                          {open === proposal.id && (
                            <ProposalDetail
                              projectId={projectId} scanId={scan.id} contentHeld={scan.contentHeld} proposal={proposal} sourceName={source}
                              link={links[proposal.id]}
                              onLink={(factKey) => setLinks((current) => ({ ...current, [proposal.id]: factKey }))}
                              answer={answers[proposal.id]}
                              onAnswer={(same) => setAnswers((current) => ({ ...current, [proposal.id]: same }))}
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
          <FactsWithoutProposal projectId={projectId} scanId={scan.id} />
        </div>

        {decide.isError && <p role="alert" className="text-xs text-destructive">{decide.error.message}</p>}
        {notice && <p role="status" className="text-xs text-muted-foreground">{notice}</p>}

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
              ? linkCount + answerCount > 0
                ? [linkCount > 0 && `${linkCount} ${linkCount === 1 ? 'match' : 'matches'} chosen.`, answerCount > 0 && `${answerCount} ${answerCount === 1 ? 'question' : 'questions'} answered.`].filter(Boolean).join(' ')
                : 'Select proposals to accept or reject.'
              : `${selected.size} selected.${added > 0 ? ` Accepting also takes ${added} that ${added === 1 ? 'it depends' : 'they depend'} on.` : ''}`}
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button variant="secondary" size="sm" disabled={selected.size === 0 || decide.isPending} onClick={() => decide.mutate({ accept: [], reject: [...selected] })}>
            Reject selected
          </Button>
          <Button size="sm" disabled={(selected.size === 0 && linkCount === 0 && answerCount === 0) || decide.isPending || !scan.canDecide || (breaking.length > 0 && !acknowledged)} onClick={() => decide.mutate({ accept: [...accepting], reject: [] })}>
            {decide.isPending ? 'Applying…' : selected.size === 0 && linkCount + answerCount > 0 ? 'Apply answers' : `Accept ${accepting.size || ''}`.trim()}
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

function ProposalDetail({ projectId, scanId, contentHeld, proposal: listed, sourceName, link, onLink, answer, onAnswer }: {
  projectId: string; scanId: string; contentHeld: boolean; proposal: Proposal; sourceName: string
  link: string | null | undefined; onLink: (factKey: string | null) => void
  answer: boolean | undefined; onAnswer: (same: boolean) => void
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
  if (proposal.action === 'bind') {
    return (
      <div className="space-y-3 pb-1">
        <BindQuestion proposal={proposal} sourceName={sourceName} answer={answer} onAnswer={onAnswer} />
        {proposal.evidence.map((evidence) => <SourceLines key={evidence.pointer} projectId={projectId} scanId={scanId} contentHeld={contentHeld} evidence={evidence} />)}
      </div>
    )
  }
  const fields = proposal.changes.length > 0 ? proposal.changes : readable(proposal.proposed ?? {})
  return (
    <div className="mt-2 space-y-3 pb-1">
      {fields.length > 0 && (
        // On a narrow screen each field is a block: its name, then each side under a label.
        <table className="w-full table-fixed text-left text-xs max-sm:block">
          <colgroup>
            <col className="w-32" />
            {proposal.changes.length > 0 && <col />}
            <col />
          </colgroup>
          <thead className="text-[10px] uppercase tracking-wider text-muted-foreground max-sm:hidden">
            <tr>
              <th className="py-1 pr-3 font-medium">Field</th>
              {proposal.changes.length > 0 && <th className="py-1 pr-3 font-medium">In the model</th>}
              <th className="py-1 font-medium">In the source</th>
            </tr>
          </thead>
          <tbody className="max-sm:block">
            {fields.map((change) => (
              <tr key={change.field} className="border-t border-border align-top max-sm:block max-sm:py-1">
                <td className="break-words py-1 pr-3 font-mono text-muted-foreground max-sm:block max-sm:pr-0">{change.field}</td>
                {proposal.changes.length > 0 && (
                  <td className="min-w-0 py-1 pr-3 max-sm:block max-sm:pr-0">
                    <span className="mb-0.5 hidden text-[10px] uppercase tracking-wider text-muted-foreground max-sm:block">In the model</span>
                    <Value value={change.from} muted />
                  </td>
                )}
                <td className="min-w-0 py-1 max-sm:block">
                  {proposal.changes.length > 0 && <span className="mb-0.5 hidden text-[10px] uppercase tracking-wider text-muted-foreground max-sm:block">In the source</span>}
                  <Value value={change.to} />
                </td>
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
      {proposal.action === 'conflict' && proposal.otherSources.length === 0 && (
        <p className="text-xs text-muted-foreground">
          A person set the value in the model. Accept to replace it with the value in the source. Reject to keep the model as it is.
        </p>
      )}
      {proposal.otherSources.length > 0 && (
        <div className="space-y-1.5">
          <p className="text-xs text-muted-foreground">
            {proposal.action === 'conflict'
              ? 'Another Source holds this. Each Source keeps its own fact, and none wins by itself. Accept to give the model the value of this scan and link it; reject to keep the model as it is.'
              : proposal.action === 'remove'
                ? 'Other Sources still hold this. Accepting removes the fact of this Source only; the entity stays in the model.'
                : 'Other Sources hold this too. Their facts stay with them.'}
          </p>
          <SourceSides proposal={proposal} sourceName={sourceName} />
        </div>
      )}
      {proposal.evidence.map((evidence) => (
        <SourceLines key={evidence.pointer} projectId={projectId} scanId={scanId} contentHeld={contentHeld} evidence={evidence} />
      ))}
    </div>
  )
}

/** The cited lines of one file at the commit of a scan, as text. A pushed Snapshot holds no lines: it says so and asks for none. */
export function SourceLines({ projectId, scanId, contentHeld, evidence }: { projectId: string; scanId: string; contentHeld: boolean; evidence: ProposalEvidence }) {
  const source = useQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'source', evidence.path, evidence.startLine, evidence.endLine],
    queryFn: () => importApi.source(projectId, scanId, evidence),
    staleTime: Infinity,
    enabled: contentHeld,
  })
  return (
    <figure className="overflow-hidden rounded-md border border-border">
      <figcaption className="flex items-center gap-2 border-b border-border bg-muted/40 px-2 py-1 font-mono text-[10px] text-muted-foreground">
        <span className="min-w-0 flex-1 truncate">{evidence.path}:{evidence.startLine}-{evidence.endLine}</span>
        <span className="shrink-0">{evidence.rule}</span>
        {evidence.url && (
          <a href={evidence.url} target="_blank" rel="noreferrer noopener" className="flex shrink-0 items-center gap-1 rounded text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary">
            GitHub <ExternalLink className="h-3 w-3" />
          </a>
        )}
      </figcaption>
      {!contentHeld ? (
        <p className="px-2 py-1.5 text-xs text-muted-foreground" title={CONTENT_NOT_HELD}>Content not held. Open these lines on the machine that pushed them.</p>
      ) : source.isError ? (
        <p className="px-2 py-1.5 text-xs text-muted-foreground">{evidence.url ? 'These lines are not stored. Open them on GitHub.' : 'This line is not stored any more.'}</p>
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

/**
 * The facts the scan found that are no proposal: the model has no place for them, or they need
 * something the source does not state. They are evidence to read, not choices to make.
 */
function FactsWithoutProposal({ projectId, scanId }: { projectId: string; scanId: string }) {
  const [open, setOpen] = useState(false)
  const facts = useInfiniteQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'evidence'],
    queryFn: ({ pageParam }) => importApi.evidence(projectId, scanId, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.cursor,
    enabled: open,
    staleTime: Infinity,
  })
  const items = facts.data?.pages.flatMap((page) => page.items) ?? []
  return (
    <details className="border-t border-border" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="cursor-pointer px-3 py-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">
        Facts with no proposal
      </summary>
      <div className="space-y-2 px-3 pb-3">
        <p className="text-xs text-muted-foreground">The scan found these in the source. They are not proposals and change nothing. Each says why.</p>
        {facts.isPending && open && <p className="text-xs text-muted-foreground">Loading…</p>}
        {facts.isError && <p role="alert" className="text-xs text-destructive">{facts.error.message}</p>}
        {facts.isSuccess && items.length === 0 && <p className="text-xs text-muted-foreground">Every fact of this scan is a proposal or matches the model.</p>}
        <ul className="space-y-1.5">
          {items.map((fact) => (
            <li key={fact.factKey} className="rounded-md border border-border px-2.5 py-1.5 text-xs">
              <p className="flex items-baseline gap-2">
                <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{fact.kind}</span>
                {/* Repository text is shown as text. */}
                <span className="min-w-0 flex-1 truncate text-foreground">{fact.name}</span>
                <span className="shrink-0 font-mono text-[10px] text-muted-foreground">
                  {fact.evidence[0] ? `${fact.evidence[0].path}:${fact.evidence[0].startLine}` : 'no source line'}
                </span>
              </p>
              <p className="mt-0.5 text-muted-foreground">{describeFactReason(fact.reason)}</p>
              {Object.keys(fact.attributes).length > 0 && (
                <p className="mt-0.5 break-words font-mono text-[10px] text-muted-foreground">
                  {Object.entries(fact.attributes).map(([key, value]) => `${key}: ${value}`).join(' · ')}
                </p>
              )}
              {fact.evidence[0]?.url && (
                <a href={fact.evidence[0].url} target="_blank" rel="noreferrer noopener" className="mt-0.5 inline-flex items-center gap-1 text-[11px] text-foreground hover:underline focus-visible:outline-2 focus-visible:outline-primary">
                  Open on GitHub <ExternalLink className="h-3 w-3" />
                </a>
              )}
            </li>
          ))}
        </ul>
        {facts.hasNextPage && (
          <Button variant="ghost" size="sm" className="h-6 text-xs" disabled={facts.isFetchingNextPage} onClick={() => facts.fetchNextPage()}>
            {facts.isFetchingNextPage ? 'Loading…' : 'Show more'}
          </Button>
        )}
      </div>
    </details>
  )
}
