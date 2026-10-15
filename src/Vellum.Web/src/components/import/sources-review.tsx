import { useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Activity, ChevronDown, ChevronRight, GitBranch, PlugZap, Radar, RotateCw, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { CONTENT_NOT_HELD, describeReview, importApi, isActive, sourceState, sourcesApi, type DecisionResult, type Proposal, type SourceSummary } from '@/lib/import-api'
import { sourcesKey, useSources } from '@/hooks/use-sources'
import { SourceSides } from './source-facts'

const ROW: Record<Proposal['action'], { sign: string; color: string; label: string }> = {
  add: { sign: '+', color: 'var(--trace-request)', label: 'Add' },
  change: { sign: '~', color: 'var(--trace-retry)', label: 'Change' },
  remove: { sign: '−', color: 'var(--trace-failure)', label: 'Remove' },
  conflict: { sign: '!', color: 'var(--trace-event)', label: 'Conflict' },
  relink: { sign: '?', color: 'var(--trace-data)', label: 'Match' },
  bind: { sign: '=', color: 'var(--trace-data)', label: 'Same as?' },
}

/** The order a person reads a Source's proposals in: questions, then disagreements, then the rest. */
const ORDER: Proposal['action'][] = ['bind', 'conflict', 'relink', 'remove', 'change', 'add']

/** How many rows of one Source the grouped view lists before it points to the full review. */
const SHOWN = 40

function short(value: string | null | undefined, length = 10): string {
  return value ? value.slice(0, length) : ''
}

/** The list of Sources on the import screen: each with its newest Snapshot and what waits for a decision. */
export function SourcesList({ projectId, onReview }: {
  projectId: string
  onReview: (source: SourceSummary) => void
}) {
  const queryClient = useQueryClient()
  const sources = useSources(projectId)
  const scan = useMutation({
    mutationFn: (id: string) => sourcesApi.scan(projectId, id, {}),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
    ]),
    onError: shownInline,
  })

  if (sources.isLoading) return <p className="px-3 py-2 text-xs text-muted-foreground">Loading Sources…</p>
  if (sources.isError) return <p role="alert" className="px-3 py-2 text-xs text-destructive">{sources.error.message}</p>
  const list = sources.data ?? []
  if (list.length === 0) return <p className="px-3 py-2 text-xs text-muted-foreground">No Source yet. Scan a repository or add a runtime Source.</p>

  return (
    <div>
      {scan.isError && <p role="alert" className="px-3 pb-1 text-xs text-[var(--trace-failure)]">{scan.error.message}</p>}
      <ul className="space-y-1 px-2 pb-2">
        {list.map((source) => (
          <li key={source.id} className="rounded-md border border-border p-2">
            <div className="flex items-start gap-2">
              <SourceIcon kind={source.kind} />
              <div className="min-w-0 flex-1">
                <p className="flex items-baseline gap-1.5">
                  <span className="truncate text-sm text-foreground">{source.name}</span>
                  <span className="shrink-0 rounded-full border border-border px-1.5 font-mono text-[10px] text-muted-foreground">{source.kind}</span>
                </p>
                <p className="truncate font-mono text-[10px] text-muted-foreground" title={source.citation}>{source.citation}</p>
                {source.latestSnapshot && (
                  <p className="font-mono text-[10px] text-muted-foreground" title={`Snapshot ${source.latestSnapshot.snapshotId}`}>
                    Snapshot {short(source.latestSnapshot.snapshotId, 8)}
                    {source.latestSnapshot.commitSha && ` · ${short(source.latestSnapshot.commitSha)}`}
                    {source.latestSnapshot.reportedCommitSha && ` · reported ${short(source.latestSnapshot.reportedCommitSha)}`}
                    {` · ${source.latestSnapshot.files} ${source.latestSnapshot.files === 1 ? 'file' : 'files'}`}
                  </p>
                )}
                {source.latestSnapshot && !source.latestSnapshot.contentHeld && (
                  <p className="mt-1 flex items-start gap-1.5 text-[11px] text-muted-foreground" title={CONTENT_NOT_HELD}>
                    <span className="shrink-0 rounded-full border border-[var(--trace-retry)]/60 px-1.5 text-[10px] text-[var(--trace-retry)]">content not held</span>
                    <span>Facts and citations only. Source lines stay on the machine that pushed.</span>
                  </p>
                )}
                {source.connection && <RuntimeStatus source={source} />}
                <p className={cn('mt-1 text-xs', source.review.pending > 0 ? 'text-foreground' : 'text-muted-foreground')}>{describeReview(source)}</p>
              </div>
            </div>
            <div className="mt-2 flex flex-wrap gap-1.5">
              {source.review.scanId && source.review.pending > 0 && (
                <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" onClick={() => onReview(source)}>Review</Button>
              )}
              {source.kind === 'agent-push' ? (
                <span className="self-center text-[11px] text-muted-foreground">Vellum does not hold these files. Run vellum scan again to update.</span>
              ) : source.kind === 'traces' ? (
                <TraceUploadButton projectId={projectId} source={source} />
              ) : (
                <Button
                  size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs"
                  disabled={scan.isPending || (source.latestScan !== null && isActive(source.latestScan.status))}
                  title={source.kind === 'newrelic' ? 'Read the account again' : 'Scan the default ref of this Source again'}
                  onClick={() => scan.mutate(source.id)}
                >
                  <RotateCw className="h-3 w-3" /> {source.kind === 'newrelic' ? 'Read again' : 'Scan again'}
                </Button>
              )}
              {source.kind === 'newrelic' && <TestConnectionButton projectId={projectId} source={source} />}
              {source.kind === 'newrelic' && <ReplaceKey projectId={projectId} source={source} />}
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}

function SourceIcon({ kind }: { kind: SourceSummary['kind'] }) {
  const Icon = kind === 'traces' ? Activity : kind === 'newrelic' ? Radar : GitBranch
  return <Icon className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
}

const TONE: Record<ReturnType<typeof sourceState>['tone'], string> = {
  ok: 'var(--trace-request)',
  busy: 'var(--trace-data)',
  warn: 'var(--trace-retry)',
  fail: 'var(--trace-failure)',
}

/** The state of a runtime Source: whether it can be read now, and what it reads. */
function RuntimeStatus({ source }: { source: SourceSummary }) {
  const state = sourceState(source)
  return (
    <>
      {source.connection?.endpoint && (
        <p className="truncate font-mono text-[10px] text-muted-foreground" title={source.connection.endpoint}>
          account {source.connection.accountId} · {source.connection.region?.toUpperCase()} · {source.connection.keySet ? 'key stored' : 'no key'}
        </p>
      )}
      <p className="mt-1 flex items-start gap-1.5 text-xs" style={{ color: TONE[state.tone] }} data-testid="source-state">
        <span className={cn('mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-current', state.tone === 'busy' && 'motion-safe:animate-pulse')} />
        <span>{state.text}</span>
      </p>
    </>
  )
}

/** Uploads another trace export to a trace Source: a new Snapshot of the same Source. */
function TraceUploadButton({ projectId, source }: { projectId: string; source: SourceSummary }) {
  const queryClient = useQueryClient()
  const picker = useRef<HTMLInputElement>(null)
  const upload = useMutation({
    mutationFn: (file: File) => sourcesApi.uploadTraces(projectId, source.id, file),
    onSuccess: () => Promise.all([
      queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
    ]),
    onError: shownInline,
  })
  return (
    <>
      <input
        ref={picker} type="file" accept=".json,application/json" className="sr-only" aria-label={`Trace file for ${source.name}`}
        onChange={(e) => { const file = e.target.files?.[0]; e.target.value = ''; if (file) upload.mutate(file) }}
      />
      <Button
        size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs"
        disabled={upload.isPending || (source.latestScan !== null && isActive(source.latestScan.status))}
        onClick={() => picker.current?.click()}
      >
        <Upload className="h-3 w-3" /> {upload.isPending ? 'Reading…' : 'Upload trace file'}
      </Button>
      {upload.isError && <p role="alert" className="basis-full text-xs text-[var(--trace-failure)]">{upload.error.message}</p>}
      {upload.data && <p role="status" className="basis-full text-xs text-muted-foreground">Read {upload.data.spans} spans: {upload.data.services} services, {upload.data.edges} edges.</p>}
    </>
  )
}

/** Stores a new key for a New Relic Source. The old one is replaced; neither is shown. */
function ReplaceKey({ projectId, source }: { projectId: string; source: SourceSummary }) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const [key, setKey] = useState('')
  const save = useMutation({
    mutationFn: () => sourcesApi.setKey(projectId, source.id, key),
    onSuccess: () => {
      setKey('')
      setOpen(false)
      return queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) })
    },
    onError: shownInline,
  })
  if (!open) {
    return <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setOpen(true)}>Replace key</Button>
  }
  return (
    <form className="flex basis-full items-center gap-1.5" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
      <Input
        type="password" autoComplete="off" value={key} onChange={(e) => setKey(e.target.value)} required spellCheck={false}
        aria-label={`New User API key for ${source.name}`} placeholder="New User API key" className="h-7 text-xs"
      />
      <Button type="submit" size="sm" className="h-7 px-2 text-xs" disabled={!key || save.isPending}>Save</Button>
      <Button type="button" size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={() => { setKey(''); setOpen(false) }}>Cancel</Button>
      {save.isError && <p role="alert" className="basis-full text-xs text-[var(--trace-failure)]">{save.error.message}</p>}
    </form>
  )
}

/** One real NerdGraph query with the stored key. */
function TestConnectionButton({ projectId, source }: { projectId: string; source: SourceSummary }) {
  const test = useMutation({ mutationFn: () => sourcesApi.testSource(projectId, source.id), onError: shownInline })
  return (
    <>
      <Button size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs" disabled={test.isPending} onClick={() => test.mutate()}>
        <PlugZap className="h-3 w-3" /> {test.isPending ? 'Testing…' : 'Test connection'}
      </Button>
      {test.data && (
        <p role="status" className={cn('basis-full text-xs', test.data.ok ? 'text-[var(--trace-request)]' : 'text-[var(--trace-failure)]')}>
          {test.data.ok ? `Connected to “${test.data.accountName}”.` : test.data.error?.message}
        </p>
      )}
      {test.isError && <p role="alert" className="basis-full text-xs text-[var(--trace-failure)]">{test.error.message}</p>}
    </>
  )
}

/**
 * The proposal diff of a project, grouped by Source. Each Source shows its pending proposals,
 * its "same as?" questions and its conflicts with other Sources, and can accept all its
 * additions, changes and removals at once. Conflicts and questions are never accepted in bulk.
 */
export function SourcesReview({ projectId, onOpenScan, onClose }: {
  projectId: string
  onOpenScan: (scanId: string, source: SourceSummary) => void
  onClose: () => void
}) {
  const sources = useSources(projectId)
  const waiting = (sources.data ?? []).filter((s) => s.review.scanId && s.review.pending > 0)
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>Proposals by Source</DialogTitle>
          <DialogDescription>
            Each Source keeps its own facts. Where two Sources disagree, both are shown and neither wins until you decide.
          </DialogDescription>
        </DialogHeader>
        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto">
          {sources.isLoading && <p className="text-sm text-muted-foreground">Loading Sources…</p>}
          {sources.isError && <p role="alert" className="text-sm text-destructive">{sources.error.message}</p>}
          {sources.data && waiting.length === 0 && <p className="text-sm text-muted-foreground">No Source has a proposal that waits for a decision.</p>}
          {waiting.map((source) => (
            <SourceGroup key={source.id} projectId={projectId} source={source} onOpenScan={() => onOpenScan(source.review.scanId!, source)} />
          ))}
        </div>
        <div className="flex justify-end">
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function SourceGroup({ projectId, source, onOpenScan }: { projectId: string; source: SourceSummary; onOpenScan: () => void }) {
  const queryClient = useQueryClient()
  const scanId = source.review.scanId!
  const proposals = useQuery({
    queryKey: ['projects', projectId, 'imports', scanId, 'proposals'],
    queryFn: () => importApi.proposals(projectId, scanId),
  })
  const [open, setOpen] = useState<string | null>(null)
  const [result, setResult] = useState<DecisionResult | null>(null)

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) })
    queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports', scanId, 'proposals'] })
    // A decision changed the model: every model query is stale.
    queryClient.invalidateQueries({ queryKey: ['projects', projectId] })
  }
  const acceptAll = useMutation({
    mutationFn: (acknowledgeImpact: boolean) => sourcesApi.acceptAll(projectId, source.id, acknowledgeImpact),
    onSuccess: (r) => { setResult(r); refresh() },
    onError: shownInline,
  })
  const answer = useMutation({
    mutationFn: ({ id, same }: { id: string; same: boolean }) =>
      importApi.decide(projectId, scanId, { accept: [], reject: [], acknowledgeImpact: false, relink: [], bind: [{ id, same }] }),
    onSuccess: (r) => { setResult(r); refresh() },
    onError: shownInline,
  })

  const pending = (proposals.data ?? []).filter((p) => p.status === 'pending')
    .sort((a, b) => ORDER.indexOf(a.action) - ORDER.indexOf(b.action))
  const impactFailure = result?.failed.some((f) => f.reason.includes('acknowledgeImpact')) ?? false

  return (
    <section aria-label={source.name} className="rounded-lg border border-border">
      <header className="flex flex-wrap items-start gap-2 border-b border-border px-3 py-2">
        <div className="min-w-0 flex-1">
          <h3 className="flex items-baseline gap-1.5 text-sm text-foreground">
            <span className="truncate">{source.name}</span>
            <span className="shrink-0 rounded-full border border-border px-1.5 font-mono text-[10px] text-muted-foreground">{source.kind}</span>
          </h3>
          <p className="truncate font-mono text-[10px] text-muted-foreground">
            {source.citation}@{short(source.latestSnapshot?.revision)}
          </p>
          <p className="mt-0.5 text-xs text-muted-foreground">{describeReview(source)}</p>
          {source.latestSnapshot && !source.latestSnapshot.contentHeld && <p className="mt-0.5 text-[11px] text-muted-foreground">{CONTENT_NOT_HELD}</p>}
        </div>
        <div className="flex shrink-0 flex-wrap gap-1.5">
          <Button size="sm" variant="ghost" className="h-7 text-xs" onClick={onOpenScan}>Open full review</Button>
          <Button
            size="sm" className="h-7 text-xs"
            disabled={source.review.acceptable === 0 || acceptAll.isPending}
            title="Accept every addition, change and removal of this Source. Conflicts and questions stay for you."
            onClick={() => acceptAll.mutate(false)}
          >
            {acceptAll.isPending ? 'Accepting…' : `Accept all from this Source (${source.review.acceptable})`}
          </Button>
        </div>
      </header>

      {(acceptAll.isError || answer.isError) && (
        <p role="alert" className="border-b border-border px-3 py-1.5 text-xs text-destructive">{(acceptAll.error ?? answer.error)!.message}</p>
      )}
      {result && result.failed.length > 0 && (
        <div role="alert" className="space-y-1 border-b border-border px-3 py-1.5 text-xs">
          <p className="text-foreground">Nothing was applied:</p>
          <ul className="list-disc space-y-0.5 pl-4 text-muted-foreground">
            {result.failed.slice(0, 5).map((f) => <li key={f.id}>{f.reason}</li>)}
          </ul>
          {impactFailure && (
            <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" onClick={() => acceptAll.mutate(true)} disabled={acceptAll.isPending}>
              I accept what this breaks. Accept all
            </Button>
          )}
        </div>
      )}
      {result && result.failed.length === 0 && (
        <p role="status" className="border-b border-border px-3 py-1.5 text-xs text-muted-foreground">
          {result.answered.length > 0
            ? result.replanned ? 'Answer applied. The proposals of this Source were planned again.' : 'Answer applied. Scan the Source again to see the new proposals.'
            : `${result.accepted.length} accepted.`}
        </p>
      )}

      {proposals.isLoading && <p className="px-3 py-2 text-xs text-muted-foreground">Loading proposals…</p>}
      {proposals.isError && <p role="alert" className="px-3 py-2 text-xs text-destructive">{proposals.error.message}</p>}
      <ul>
        {pending.slice(0, SHOWN).map((proposal) => {
          const row = ROW[proposal.action]
          const expandable = proposal.otherSources.length > 0
          const isOpen = open === proposal.id
          return (
            <li key={proposal.id} className="border-b border-border px-3 py-1.5 last:border-b-0">
              <div className="flex items-center gap-2">
                <span className="flex h-4 w-4 shrink-0 items-center justify-center rounded border font-mono text-[11px]" style={{ borderColor: row.color, color: row.color }} aria-hidden="true">{row.sign}</span>
                <span className="w-16 shrink-0 text-[11px] font-medium" style={{ color: row.color }}>{row.label}</span>
                <span className="w-20 shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{proposal.entityKind}</span>
                {expandable ? (
                  <button
                    type="button" aria-expanded={isOpen} onClick={() => setOpen(isOpen ? null : proposal.id)}
                    className="flex min-w-0 flex-1 items-center gap-1 rounded text-left text-sm text-foreground focus-visible:outline-2 focus-visible:outline-primary"
                  >
                    {isOpen ? <ChevronDown className="h-3 w-3 shrink-0" /> : <ChevronRight className="h-3 w-3 shrink-0" />}
                    <span className="truncate">
                      {proposal.action === 'bind' ? `“${proposal.name}” same as “${(proposal.current?.name as string) ?? ''}”?` : proposal.name}
                    </span>
                  </button>
                ) : (
                  <span className="min-w-0 flex-1 truncate text-sm text-foreground">{proposal.name}</span>
                )}
                {proposal.action === 'bind' ? (
                  <span className="flex shrink-0 gap-1">
                    <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" disabled={answer.isPending}
                      title="The same element: bind it to this Source" onClick={() => answer.mutate({ id: proposal.id, same: true })}>
                      Same
                    </Button>
                    <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={answer.isPending}
                      title="Another thing: propose a new element" onClick={() => answer.mutate({ id: proposal.id, same: false })}>
                      Not the same
                    </Button>
                  </span>
                ) : (
                  <span className="hidden shrink-0 font-mono text-[10px] text-muted-foreground sm:inline">
                    {proposal.evidence[0] ? `${proposal.evidence[0].path}:${proposal.evidence[0].startLine}` : ''}
                  </span>
                )}
              </div>
              {isOpen && <div className="mt-2"><SourceSides proposal={proposal} sourceName={source.name} /></div>}
            </li>
          )
        })}
      </ul>
      {pending.length > SHOWN && (
        <p className="px-3 py-1.5 text-xs text-muted-foreground">
          And {pending.length - SHOWN} more. <button type="button" className="underline underline-offset-2 hover:text-foreground" onClick={onOpenScan}>Open the full review</button>.
        </p>
      )}
    </section>
  )
}
