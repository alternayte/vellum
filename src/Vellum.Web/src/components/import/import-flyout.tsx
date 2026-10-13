import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GitBranch, RotateCw, Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { describeScan, importApi, isActive, type ScanSummary } from '@/lib/import-api'
import { ProposalReview } from './proposal-review'
import { ImpactDialog } from './impact-dialog'

const STATUS_COLOR: Record<ScanSummary['status'], string> = {
  queued: 'var(--muted-foreground)',
  running: 'var(--trace-data)',
  waiting: 'var(--trace-retry)',
  completed: 'var(--trace-request)',
  failed: 'var(--trace-failure)',
  cancelled: 'var(--muted-foreground)',
}

/**
 * Imports read a GitHub repository at one commit and propose what they found. Nothing
 * reaches the model until a person accepts it in the review.
 */
export function ImportFlyout({ projectId, owners }: {
  projectId: string
  /** The element that owns an operation, a subscription or a message: what the canvas shows for it. */
  owners: Record<string, string>
}) {
  const queryClient = useQueryClient()
  const [repository, setRepository] = useState('')
  const [ref, setRef] = useState('')
  const [folders, setFolders] = useState('')
  const [reviewing, setReviewing] = useState<ScanSummary | null>(null)
  const [impactOf, setImpactOf] = useState<ScanSummary | null>(null)

  const scans = useQuery({
    queryKey: ['projects', projectId, 'imports'],
    queryFn: () => importApi.list(projectId),
    // Poll only while a scan is moving.
    refetchInterval: (query) => (query.state.data?.some((s) => isActive(s.status)) ? 2000 : false),
  })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] })
  const start = useMutation({
    mutationFn: () => {
      const includePaths = folders.split(/[,\n]/).map((f) => f.trim()).filter(Boolean)
      return importApi.start(projectId, { repository: repository.trim(), ref: ref.trim() || undefined, includePaths: includePaths.length > 0 ? includePaths : undefined })
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
        <label className="block text-[11px] font-medium text-muted-foreground">
          Branch, tag or commit
          <Input className="mt-1" value={ref} onChange={(e) => setRef(e.target.value)} placeholder="The default branch" spellCheck={false} />
        </label>
        <label className="block text-[11px] font-medium text-muted-foreground">
          Only these folders
          <Input className="mt-1" value={folders} onChange={(e) => setFolders(e.target.value)} placeholder="All folders. Separate with commas." spellCheck={false} />
        </label>
        <Button type="submit" size="sm" className="w-full" disabled={start.isPending || !repository.trim()}>
          {start.isPending ? 'Starting…' : 'Scan repository'}
        </Button>
        <p className="text-[11px] leading-relaxed text-muted-foreground">
          Vellum reads the files at one commit. It does not run the code, and it does not change the model until you accept a proposal.
        </p>
      </form>
      {failure && <p role="alert" className="border-b border-border px-3 py-2 text-xs text-[var(--trace-failure)]">{failure.message}</p>}

      <div className="flex-1 overflow-y-auto p-2">
        {scans.data?.length === 0 && <p className="px-1.5 py-1 text-xs text-muted-foreground">No scan yet.</p>}
        <ul className="space-y-1">
          {scans.data?.map((scan) => (
            <li key={scan.id} className="rounded-md border border-border p-2">
              <div className="flex items-start gap-2">
                <GitBranch className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm text-foreground">{scan.repository}</p>
                  <p className="break-words font-mono text-[10px] text-muted-foreground">
                    {scan.ref ?? scan.trackedRef ?? 'default branch'}{scan.commitSha ? ` · ${scan.commitSha.slice(0, 10)}` : ''}
                    {scan.kind === 'comparison' && ' · comparison'}
                  </p>
                </div>
              </div>
              <p className="mt-1.5 flex items-start gap-1.5 text-xs" style={{ color: STATUS_COLOR[scan.status] }}>
                <span className={cn('mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-current', scan.status === 'running' && 'motion-safe:animate-pulse')} />
                <span className={scan.status === 'failed' || !scan.isComplete ? '' : 'text-muted-foreground'}>{describeScan(scan)}</span>
              </p>
              <div className="mt-2 flex flex-wrap items-center gap-1.5">
                {scan.status === 'completed' && (
                  <Button size="sm" variant="secondary" className="h-6 px-2 text-xs" onClick={() => setReviewing(scan)}>
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
                {(scan.status === 'failed' || scan.status === 'cancelled') && (
                  <Button size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs" onClick={() => resume.mutate(scan.id)} disabled={resume.isPending}>
                    <RotateCw className="h-3 w-3" /> Resume
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
      </div>

      {impactOf && <ImpactDialog projectId={projectId} scan={impactOf} owners={owners} onClose={() => setImpactOf(null)} />}
      {reviewing && <ProposalReview projectId={projectId} scan={reviewing} onClose={() => { setReviewing(null); refresh() }} />}
    </div>
  )
}
