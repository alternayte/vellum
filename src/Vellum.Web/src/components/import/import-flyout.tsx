import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GitBranch, GitPullRequest, Plus, RotateCw, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { describeScan, importApi, isActive, pullRequestState, scanSubject, type ScanSummary, type SourceSummary } from '@/lib/import-api'
import { ProposalReview } from './proposal-review'
import { ImpactDialog } from './impact-dialog'
import { SourcesList, SourcesReview } from './sources-review'
import { AddSourceDialog } from './add-source-dialog'
import { BulkAddDialog } from './bulk-add-dialog'
import { sourcesKey } from '@/hooks/use-sources'

const STATUS_COLOR: Record<ScanSummary['status'], string> = {
  queued: 'var(--muted-foreground)',
  running: 'var(--trace-data)',
  waiting: 'var(--trace-retry)',
  completed: 'var(--trace-request)',
  failed: 'var(--trace-failure)',
  cancelled: 'var(--muted-foreground)',
}

/** What a scan reads: a branch or a tag, one commit, or the head commit of a pull request. */
type Target = 'ref' | 'commit' | 'pull'

const TARGETS: { value: Target; label: string; field: string; placeholder: string }[] = [
  { value: 'ref', label: 'Branch', field: 'Branch or tag', placeholder: 'The default branch' },
  { value: 'commit', label: 'Commit SHA', field: 'Commit SHA', placeholder: '40 hexadecimal characters' },
  { value: 'pull', label: 'Pull request #', field: 'Pull request number', placeholder: 'For example 12' },
]

/**
 * Imports read a Source, such as a GitHub repository at one commit, and propose what they
 * found. Each completed scan is a Snapshot of its Source. Nothing reaches the model until a
 * person accepts it in the review.
 */
export function ImportFlyout({ projectId, owners }: {
  projectId: string
  /** The element that owns an operation, a subscription or a message: what the canvas shows for it. */
  owners: Record<string, string>
}) {
  const queryClient = useQueryClient()
  const [repository, setRepository] = useState('')
  const [target, setTarget] = useState<Target>('ref')
  const [ref, setRef] = useState('')
  const [folders, setFolders] = useState('')
  const [reviewing, setReviewing] = useState<{ scan: ScanSummary; sourceName?: string } | null>(null)
  const [bySource, setBySource] = useState(false)
  const [opening, setOpening] = useState<string | null>(null)
  const [impactOf, setImpactOf] = useState<ScanSummary | null>(null)
  const [adding, setAdding] = useState<'one' | 'many' | null>(null)

  const scans = useQuery({
    queryKey: ['projects', projectId, 'imports'],
    queryFn: () => importApi.list(projectId),
    // Poll fast while a scan is moving, and slowly otherwise: a scan that CI, the API or another
    // session starts must also show. The browser reports a page as hidden when its window is
    // covered or in the background, and the default skips every poll then, so the list stayed
    // on the first state it read ("Listing files") until a reload.
    refetchInterval: (query) => (query.state.data?.some((s) => isActive(s.status)) ? 2000 : 15000),
    refetchIntervalInBackground: true,
  })
  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
    queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
  ])
  /** Opens the full review of the scan whose proposals a Source waits on. */
  const openScan = async (scanId: string, source: SourceSummary) => {
    setOpening(scanId)
    try {
      setReviewing({ scan: await importApi.get(projectId, scanId), sourceName: source.name })
    } finally {
      setOpening(null)
    }
  }
  const start = useMutation({
    mutationFn: () => {
      const includePaths = folders.split(/[,\n]/).map((f) => f.trim()).filter(Boolean)
      const value = ref.trim()
      return importApi.start(projectId, {
        repository: repository.trim(),
        ref: target !== 'pull' && value ? value : undefined,
        pullRequest: target === 'pull' ? Number(value.replace(/^#/, '')) : undefined,
        includePaths: includePaths.length > 0 ? includePaths : undefined,
      })
    },
    onSuccess: () => { setRepository(''); setRef(''); setFolders(''); return refresh() },
    onError: shownInline,
  })
  const cancel = useMutation({ mutationFn: (id: string) => importApi.cancel(projectId, id), onSuccess: refresh, onError: shownInline })
  const resume = useMutation({ mutationFn: (id: string) => importApi.resume(projectId, id), onSuccess: refresh, onError: shownInline })
  const track = useMutation({ mutationFn: (id: string) => importApi.track(projectId, id), onSuccess: refresh, onError: shownInline })
  const failure = start.error ?? cancel.error ?? resume.error ?? track.error

  const submit = (e: FormEvent) => {
    e.preventDefault()
    start.mutate()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Import from GitHub</span>
      </div>

      <form onSubmit={submit} className="space-y-2 border-b border-border p-3">
        <label className="block text-[11px] font-medium text-muted-foreground">
          Repository
          <Input className="mt-1" value={repository} onChange={(e) => setRepository(e.target.value)} placeholder="owner/name or a GitHub URL" required spellCheck={false} />
        </label>
        <fieldset>
          <legend className="text-[11px] font-medium text-muted-foreground">Read</legend>
          <div role="radiogroup" aria-label="What to read" className="mt-1 grid grid-cols-3 rounded-md border border-border p-0.5">
            {TARGETS.map((option) => (
              <button
                key={option.value}
                type="button"
                role="radio"
                aria-checked={target === option.value}
                onClick={() => { setTarget(option.value); setRef('') }}
                className={cn(
                  'rounded px-1 py-1 text-[11px] text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                  target === option.value && 'bg-muted text-foreground',
                )}
              >
                {option.label}
              </button>
            ))}
          </div>
        </fieldset>
        <label className="block text-[11px] font-medium text-muted-foreground">
          {TARGETS.find((t) => t.value === target)!.field}
          <Input
            className="mt-1" value={ref} onChange={(e) => setRef(e.target.value)}
            placeholder={TARGETS.find((t) => t.value === target)!.placeholder} spellCheck={false}
            required={target !== 'ref'}
            inputMode={target === 'pull' ? 'numeric' : undefined}
            pattern={target === 'pull' ? '#?[1-9][0-9]*' : target === 'commit' ? '[0-9a-fA-F]{40}' : undefined}
            title={target === 'pull' ? 'A pull request number, such as 12' : target === 'commit' ? 'A full commit SHA of 40 hexadecimal characters' : undefined}
          />
        </label>
        {target === 'pull' && (
          <p className="text-[11px] leading-relaxed text-muted-foreground">
            Vellum reads the head commit of the pull request and compares it with the model. It writes nothing to GitHub.
          </p>
        )}
        <label className="block text-[11px] font-medium text-muted-foreground">
          Only these folders
          <Input className="mt-1" value={folders} onChange={(e) => setFolders(e.target.value)} placeholder="All folders. Separate with commas." spellCheck={false} />
        </label>
        <Button type="submit" size="sm" className="w-full" disabled={start.isPending || !repository.trim()}>
          {start.isPending ? 'Starting…' : 'Scan repository'}
        </Button>
        <p className="text-[11px] leading-relaxed text-muted-foreground">
          Vellum reads the files at one commit. The repository becomes a Source of this project, and each scan a Snapshot of it. Vellum does not run the code, and it does not change the model until you accept a proposal.
        </p>
      </form>
      {failure && <p role="alert" className="border-b border-border px-3 py-2 text-xs text-[var(--trace-failure)]">{failure.message}</p>}

      <div className="flex-1 overflow-y-auto">
        <section aria-labelledby="import-sources" className="border-b border-border pt-2">
          <div className="flex items-center gap-2 px-3 pb-1.5">
            <h3 id="import-sources" className="flex-1 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Sources</h3>
            <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setBySource(true)}>Proposals by Source</Button>
          </div>
          <div className="flex gap-1.5 px-2 pb-2">
            <Button size="sm" variant="secondary" className="h-6 flex-1 gap-1 px-2 text-xs" onClick={() => setAdding('one')}>
              <Plus className="h-3 w-3" /> Add Source
            </Button>
            <Button size="sm" variant="secondary" className="h-6 flex-1 px-2 text-xs" title="Add many repositories of one organization, group or workspace" onClick={() => setAdding('many')}>
              Add many repositories
            </Button>
          </div>
          <SourcesList projectId={projectId} onReview={(source) => openScan(source.review.scanId!, source)} />
          {opening && <p role="status" className="px-3 pb-2 text-xs text-muted-foreground">Opening the review…</p>}
        </section>
        <h3 className="px-3 pb-1 pt-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Scans</h3>
        <div className="px-2 pb-2">
        {scans.data?.length === 0 && <p className="px-1.5 py-1 text-xs text-muted-foreground">No scan yet.</p>}
        <ul className="space-y-1">
          {scans.data?.map((scan) => (
            <li key={scan.id} className="rounded-md border border-border p-2">
              <div className="flex items-start gap-2">
                {scan.pullRequestNumber !== null
                  ? <GitPullRequest className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                  : <GitBranch className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />}
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm text-foreground">{scan.repository}</p>
                  <p className="break-words font-mono text-[10px] text-muted-foreground">
                    {scanSubject(scan)}{scan.commitSha ? ` · ${scan.commitSha.slice(0, 10)}` : ''}
                    {scan.kind === 'comparison' && ' · comparison'}
                  </p>
                  {scan.pullRequest && (
                    // The title is repository text, shown as text.
                    <p className="mt-0.5 break-words text-xs text-foreground">
                      {scan.pullRequest.title ?? 'No title'}
                      <span className="ml-1.5 text-[10px] text-muted-foreground">
                        {pullRequestState(scan.pullRequest)} · {scan.pullRequest.headRef} → {scan.pullRequest.baseRef}
                        {scan.pullRequest.fromFork && ' · from a fork'}
                      </span>
                    </p>
                  )}
                </div>
              </div>
              <p className="mt-1.5 flex items-start gap-1.5 text-xs" style={{ color: STATUS_COLOR[scan.status] }}>
                <span className={cn('mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-current', scan.status === 'running' && 'motion-safe:animate-pulse')} />
                <span className={scan.status === 'failed' || !scan.isComplete ? '' : 'text-muted-foreground'}>{describeScan(scan)}</span>
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-1.5">
                {scan.status === 'completed' && (
                  <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" onClick={() => setReviewing({ scan })}>
                    {scan.proposalCounts.pending > 0 ? `Review ${scan.proposalCounts.pending} ${scan.proposalCounts.pending === 1 ? 'proposal' : 'proposals'}` : 'Open review'}
                  </Button>
                )}
                {scan.status === 'completed' && (
                  <Button
                    size="sm" variant="secondary" className="h-6 px-2 text-xs"
                    title="What this scan changes, and which flows and promises that reaches"
                    onClick={() => setImpactOf(scan)}
                  >
                    Impact
                  </Button>
                )}
                {scan.status === 'completed' && scan.kind === 'comparison' && scan.ref && (
                  <Button
                    size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={track.isPending}
                    title="Make this ref the one the model follows. Its proposals can then be accepted."
                    onClick={() => track.mutate(scan.id)}
                  >
                    Follow this ref
                  </Button>
                )}
                {isActive(scan.status) && (
                  <Button size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs" onClick={() => cancel.mutate(scan.id)} disabled={cancel.isPending}>
                    <Square className="h-3 w-3" /> Cancel
                  </Button>
                )}
                {scan.canResume && (
                  <Button size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs" onClick={() => resume.mutate(scan.id)} disabled={resume.isPending}>
                    <RotateCw className="h-3 w-3" /> Resume
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
        </div>
      </div>

      {adding === 'one' && <AddSourceDialog projectId={projectId} onClose={() => setAdding(null)} />}
      {adding === 'many' && <BulkAddDialog projectId={projectId} onClose={() => setAdding(null)} />}
      {impactOf && <ImpactDialog projectId={projectId} scan={impactOf} owners={owners} onClose={() => setImpactOf(null)} />}
      {bySource && !reviewing && (
        <SourcesReview projectId={projectId} onOpenScan={(scanId, source) => openScan(scanId, source)} onClose={() => { setBySource(false); refresh() }} />
      )}
      {reviewing && (
        <ProposalReview projectId={projectId} scan={reviewing.scan} sourceName={reviewing.sourceName} onClose={() => { setReviewing(null); refresh() }} />
      )}
    </div>
  )
}
