import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { prettyJson } from '@/lib/pretty-json'
import {
  importApi, withDependencies,
  type DecisionResult, type NeedsYouSource, type Proposal, type ProposalAction, type ProposalEvidence,
} from '@/lib/import-api'
import { useKeepFact, useNeedsYou, useNeedsYouDecision, type NeedsYouDecision } from '@/hooks/use-needs-you'
import { SourceSides } from './source-facts'
import { SourceLines } from './source-lines'

const ACTION: Record<ProposalAction, { label: string; color: string; sign: string }> = {
  add: { label: 'Add', color: 'var(--trace-request)', sign: '+' },
  change: { label: 'Change', color: 'var(--trace-retry)', sign: '~' },
  remove: { label: 'Not seen', color: 'var(--trace-failure)', sign: '−' },
  conflict: { label: 'Conflict', color: 'var(--trace-event)', sign: '!' },
  relink: { label: 'Match', color: 'var(--trace-data)', sign: '?' },
  bind: { label: 'Same as?', color: 'var(--trace-data)', sign: '=' },
}

/** An addition that a rule inferred from a name. The scan never applies it; a person accepts or rejects it. */
const SUGGESTION = { label: 'Suggestion', color: 'var(--trace-data)', sign: '≈' }

const isSuggestion = (proposal: Proposal) => proposal.action === 'add' && proposal.factStatus === 'inferred'

/** "60%" for 0.6. */
const percent = (confidence: number | null | undefined) =>
  confidence === null || confidence === undefined ? null : `${Math.round(confidence * 100)}%`

/** The order a person reads the list in: questions and conflicts first, additions last. */
const ORDER: ProposalAction[] = ['bind', 'conflict', 'relink', 'change', 'remove', 'add']

/** A pushed Snapshot holds no file content, so no source line can be shown for it. */
const contentHeld = (source: NeedsYouSource) => source.sourceKind !== 'agent-push'

/** The text that refuses an accept which breaks a scenario or another part until the person says so. */
const needsAcknowledgement = (result: DecisionResult) => result.failed.some((f) => f.reason.includes('acknowledgeImpact'))

/**
 * "Needs you": what the scans did not apply by themselves. A scan applies every addition and
 * every change of a fact that only its Source wrote; a change of a fact a person edited, a fact
 * the latest Snapshot no longer holds, and each question wait here for the person.
 */
export function NeedsYouList({ projectId, readOnly = false }: { projectId: string; readOnly?: boolean }) {
  const needsYou = useNeedsYou(projectId)
  const total = (needsYou.data ?? []).reduce((sum, source) => sum + source.pending, 0)
  return (
    <section aria-labelledby="needs-you" className="border-b border-border pt-2">
      <h3 id="needs-you" className="flex items-baseline gap-1.5 px-3 pb-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        Needs you
        {total > 0 && <span className="rounded-full bg-[var(--trace-retry)]/15 px-1.5 font-mono text-[10px] normal-case tracking-normal text-[var(--trace-retry)]">{total}</span>}
      </h3>
      {needsYou.isLoading && <p className="px-3 pb-2 text-xs text-muted-foreground">Loading…</p>}
      {needsYou.isError && <p role="alert" className="px-3 pb-2 text-xs text-destructive">{needsYou.error.message}</p>}
      {needsYou.data && total === 0 && (
        <p className="px-3 pb-2 text-xs text-muted-foreground">Nothing needs you. Each scan applies what it observes; a change to a fact you edited waits here.</p>
      )}
      <div className="space-y-2 px-2 pb-2">
        {needsYou.data?.map((source) => <SourceItems key={source.sourceId} projectId={projectId} source={source} readOnly={readOnly} />)}
      </div>
    </section>
  )
}

function SourceItems({ projectId, source, readOnly }: { projectId: string; source: NeedsYouSource; readOnly: boolean }) {
  const items = [...source.items].sort((a, b) => ORDER.indexOf(a.action) - ORDER.indexOf(b.action))
  return (
    <div className="rounded-md border border-border">
      <p className="flex items-baseline gap-1.5 border-b border-border px-2 py-1">
        <span className="min-w-0 flex-1 truncate text-xs text-foreground">{source.sourceName}</span>
        <span className="shrink-0 rounded-full border border-border px-1.5 font-mono text-[10px] text-muted-foreground">{source.sourceKind}</span>
      </p>
      <ul>
        {items.map((proposal) => <NeedsYouRow key={proposal.id} projectId={projectId} source={source} proposal={proposal} readOnly={readOnly} />)}
      </ul>
      {source.pending > source.items.length && (
        <p className="px-2 py-1 text-[11px] text-muted-foreground">And {source.pending - source.items.length} more. Decide these first.</p>
      )}
    </div>
  )
}

/** One line of what a change says: the first changed field, from and to. */
function changeLine(proposal: Proposal): string | null {
  const change = proposal.changes.find((c) => !c.field.startsWith('observed.')) ?? proposal.changes[0]
  if (!change) return null
  const show = (value: unknown) => (value === null || value === undefined || value === '' ? 'empty' : typeof value === 'string' ? value : JSON.stringify(value))
  return `${change.field}: ${show(change.from)} → ${show(change.to)}`
}

function NeedsYouRow({ projectId, source, proposal, readOnly }: { projectId: string; source: NeedsYouSource; proposal: Proposal; readOnly: boolean }) {
  const [open, setOpen] = useState(false)
  const [link, setLink] = useState<string | null | undefined>(undefined)
  const decide = useNeedsYouDecision(projectId)
  const keep = useKeepFact(projectId)
  const suggestion = isSuggestion(proposal)
  const action = suggestion ? SUGGESTION : ACTION[proposal.action]
  const send = (decision: NeedsYouDecision) => decide.mutate({ scanId: source.scanId, decision })
  const accept = (acknowledgeImpact = false) =>
    send({ accept: [...withDependencies(new Set([proposal.id]), source.items)], acknowledgeImpact })
  const result = decide.data
  const failures = result?.failed ?? []
  const busy = decide.isPending || keep.isPending
  const line = proposal.action === 'change' || proposal.action === 'conflict' ? changeLine(proposal) : null

  return (
    <li className="border-b border-border px-2 py-1.5 last:border-b-0">
      <div className="flex items-start gap-1.5">
        <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded border font-mono text-[11px]" style={{ borderColor: action.color, color: action.color }} aria-hidden="true">{action.sign}</span>
        <button
          type="button" aria-expanded={open} onClick={() => setOpen(!open)}
          className="min-w-0 flex-1 rounded text-left focus-visible:outline-2 focus-visible:outline-primary"
        >
          <span className="flex items-baseline gap-1.5">
            <span className="shrink-0 text-[11px] font-medium" style={{ color: action.color }}>{action.label}</span>
            <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{proposal.entityKind}</span>
            {open ? <ChevronDown className="h-3 w-3 shrink-0 self-center text-muted-foreground" /> : <ChevronRight className="h-3 w-3 shrink-0 self-center text-muted-foreground" />}
          </span>
          {/* Repository text is shown as text. */}
          <span className="block truncate text-sm text-foreground">
            {proposal.action === 'bind' ? `“${proposal.name}” same as “${(proposal.current?.name as string) ?? ''}”?` : proposal.name}
          </span>
          {line && <span className="block truncate font-mono text-[10px] text-muted-foreground" title={line}>{line}</span>}
          {proposal.action === 'remove' && <span className="block text-[11px] text-muted-foreground">Not seen in the latest scan. Nothing was deleted.</span>}
          {suggestion && (
            <span className="block text-[11px] text-muted-foreground">
              Inferred{percent(proposal.confidence) && `, confidence ${percent(proposal.confidence)}`}. Not in the model until you accept it.
            </span>
          )}
        </button>
      </div>

      {!readOnly && proposal.canDecide && (
        <div className="mt-1.5 flex flex-wrap gap-1 pl-5">
          {(proposal.action === 'add' || proposal.action === 'change' || proposal.action === 'conflict') && (
            <>
              <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy}
                title={suggestion ? 'Add it to the model, labelled inferred' : undefined}
                onClick={() => accept()}>
                Accept
              </Button>
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={busy}
                title={proposal.action === 'conflict' ? 'Keep the model as it is. The same proposal is not made again.' : 'The same proposal is not made again.'}
                onClick={() => send({ reject: [proposal.id] })}>
                Reject
              </Button>
            </>
          )}
          {proposal.action === 'remove' && (
            <>
              <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy} title="Remove it from the model" onClick={() => accept()}>Remove</Button>
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={busy}
                title="Keep it. The mark goes until a later Snapshot changes the fact."
                onClick={() => keep.mutate({ entityKind: proposal.entityKind, entityId: proposal.entityId })}>
                Keep
              </Button>
            </>
          )}
          {proposal.action === 'bind' && (
            <>
              <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy} title="The same element: bind it to this Source" onClick={() => send({ bind: [{ id: proposal.id, same: true }] })}>Same</Button>
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={busy} title="Another thing: propose a new element" onClick={() => send({ bind: [{ id: proposal.id, same: false }] })}>Not the same</Button>
            </>
          )}
          {proposal.action === 'relink' && (
            open
              ? <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy || link === undefined} onClick={() => send({ relink: [{ id: proposal.id, factKey: link ?? null }] })}>Apply</Button>
              : <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setOpen(true)}>Choose</Button>
          )}
        </div>
      )}
      {!proposal.canDecide && proposal.canDecideReason && <p className="mt-1 pl-5 text-[11px] text-muted-foreground">{proposal.canDecideReason}</p>}

      {(decide.isError || keep.isError) && <p role="alert" className="mt-1 pl-5 text-xs text-destructive">{(decide.error ?? keep.error)!.message}</p>}
      {failures.length > 0 && result && (
        <div role="alert" className="mt-1 space-y-1 pl-5 text-xs">
          <ul className="space-y-0.5 text-muted-foreground">{failures.slice(0, 3).map((f) => <li key={f.id}>{f.reason}</li>)}</ul>
          {needsAcknowledgement(result) && (
            <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={busy} onClick={() => accept(true)}>I accept what this breaks</Button>
          )}
        </div>
      )}

      {open && (
        <div className="mt-2 pl-5">
          <ProposalDetail projectId={projectId} source={source} proposal={proposal} link={link} onLink={setLink} />
        </div>
      )}
    </li>
  )
}

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return 'empty'
  return typeof value === 'string' ? value : JSON.stringify(value)
}

/** A value of a proposal: a JSON object or array (a schema) indented in a scrolling block, anything else a line of text. */
function Value({ value, muted }: { value: unknown; muted?: boolean }) {
  const pretty = prettyJson(value)
  if (pretty === null) return <span className={muted ? 'break-all text-muted-foreground' : 'break-all text-foreground'}>{show(value)}</span>
  return (
    // Repository text is shown as text. It is never rendered as markup.
    <pre
      tabIndex={0}
      className={cn('max-h-56 overflow-auto rounded-md border border-border bg-background px-2 py-1.5 font-mono text-[11px] leading-relaxed focus-visible:outline-2 focus-visible:outline-primary', muted ? 'text-muted-foreground' : 'text-foreground')}
    >
      {pretty}
    </pre>
  )
}

/** The proposed values as rows: what the source showed, one attribute per row, and a name in place of the ID it stands for. */
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

function ProposalDetail({ projectId, source, proposal: listed, link, onLink }: {
  projectId: string; source: NeedsYouSource; proposal: Proposal
  link: string | null | undefined; onLink: (factKey: string | null) => void
}) {
  // The list cuts large bodies; the detail loads the whole proposal when it was cut.
  const whole = useQuery({
    queryKey: ['projects', projectId, 'imports', source.scanId, 'proposal', listed.id],
    queryFn: () => importApi.proposal(projectId, source.scanId, listed.id),
    enabled: listed.truncated,
    staleTime: Infinity,
  })
  const proposal = whole.data ?? listed
  const lines = proposal.evidence.map((evidence) => (
    <SourceLines key={evidence.pointer} projectId={projectId} scanId={source.scanId} contentHeld={contentHeld(source)} evidence={evidence} />
  ))
  if (proposal.action === 'relink') return <RelinkChoice proposal={proposal} link={link} onLink={onLink} />
  if (proposal.action === 'bind') {
    return (
      <div className="space-y-2 pb-1">
        <p className="text-xs text-muted-foreground">
          {source.sourceName} has “{proposal.name}”, and the model has an element of that name. Same binds the element to this Source; Not the same adds a new element.
        </p>
        <SourceSides proposal={proposal} sourceName={source.sourceName} />
        {lines}
      </div>
    )
  }
  const fields = proposal.changes.length > 0 ? proposal.changes : readable(proposal.proposed ?? {})
  return (
    <div className="space-y-2 pb-1 text-xs">
      {proposal.action === 'conflict' && proposal.otherSources.length === 0 && (
        <p className="text-muted-foreground">A person set the value in the model. Accept to take the value of the source. Reject to keep the model as it is.</p>
      )}
      {proposal.action === 'remove' && (
        <p className="text-muted-foreground">The latest Snapshot of {source.sourceName} no longer holds this. Remove takes it out of the model. Keep leaves it and clears the mark.</p>
      )}
      {isSuggestion(proposal) && (
        <p className="text-muted-foreground">
          A rule inferred this from a name in {source.sourceName}. A name is a hint, not proof. Accept adds it to the model, labelled inferred.
          Reject keeps it out, and the same suggestion is not made again.
        </p>
      )}
      {fields.length > 0 && (
        <dl className="space-y-1">
          {fields.map((change) => (
            <div key={change.field} className="border-t border-border pt-1">
              <dt className="font-mono text-[10px] text-muted-foreground">{change.field}</dt>
              <dd className="space-y-0.5">
                {proposal.changes.length > 0 && <div><span className="mr-1 text-[10px] uppercase tracking-wider text-muted-foreground">Model</span><Value value={change.from} muted /></div>}
                <div>{proposal.changes.length > 0 && <span className="mr-1 text-[10px] uppercase tracking-wider text-muted-foreground">Source</span>}<Value value={change.to} /></div>
              </dd>
            </div>
          ))}
        </dl>
      )}
      {proposal.impact && (proposal.impact.scenarios.length > 0 || proposal.impact.entities.length > 0) && (
        <div className="rounded-md border border-[var(--trace-failure)]/60 px-2 py-1.5">
          <p className="font-medium text-foreground">Accepting this breaks:</p>
          <ul className="mt-1 space-y-0.5 text-muted-foreground">
            {proposal.impact.scenarios.map((s) => <li key={s.id}>Scenario “{s.name}”, {s.stepIds.length === 1 ? 'step' : 'steps'} <span className="font-mono">{s.stepIds.join(', ')}</span></li>)}
            {proposal.impact.entities.map((e) => <li key={e.id}>{e.entityKind} {e.name}</li>)}
          </ul>
        </div>
      )}
      {proposal.otherSources.length > 0 && (
        <div className="space-y-1.5">
          <p className="text-muted-foreground">
            {proposal.action === 'remove'
              ? 'Other Sources still hold this. Remove takes away the fact of this Source only; the entity stays.'
              : 'Another Source holds this. Each Source keeps its own fact, and none wins by itself.'}
          </p>
          <SourceSides proposal={proposal} sourceName={source.sourceName} />
        </div>
      )}
      {lines}
    </div>
  )
}

/** The extraction rules changed, and no exact rule says which fact of this scan the model already holds. A person chooses. */
function RelinkChoice({ proposal, link, onLink }: { proposal: Proposal; link: string | null | undefined; onLink: (factKey: string | null) => void }) {
  const candidates = ((proposal.proposed as { candidates?: { factKey: string; name: string; evidence: ProposalEvidence[] }[] } | null)?.candidates) ?? []
  const name = `relink-${proposal.id}`
  return (
    <fieldset className="space-y-1.5 pb-1 text-xs">
      <legend className="text-muted-foreground">Which fact of this scan is “{proposal.name}”?</legend>
      {candidates.map((candidate) => (
        <label key={candidate.factKey} className="flex items-start gap-2 rounded-md border border-border px-2 py-1.5">
          <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={link === candidate.factKey} onChange={() => onLink(candidate.factKey)} />
          <span className="min-w-0">
            <span className="text-foreground">{candidate.name}</span>
            {candidate.evidence[0] && <span className="block font-mono text-[10px] text-muted-foreground">{candidate.evidence[0].path}:{candidate.evidence[0].startLine}-{candidate.evidence[0].endLine}</span>}
          </span>
        </label>
      ))}
      <label className="flex items-start gap-2 rounded-md border border-border px-2 py-1.5">
        <input type="radio" name={name} className="mt-0.5 accent-[var(--primary)]" checked={link === null} onChange={() => onLink(null)} />
        <span className="text-foreground">None of these. The source no longer has it.</span>
      </label>
    </fieldset>
  )
}
