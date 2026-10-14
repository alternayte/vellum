import { useState } from 'react'
import { useInfiniteQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { RESULT_COLOR, RESULT_LABEL, promisesApi, runActor, type RunStarter, type VerificationRun } from '@/lib/promises-api'

interface Props {
  projectId: string
  promiseId: string
  obligationId: string
  /** The name of a model stream or a scan of this project, when one is known. */
  nameOf: (id: string) => string | undefined
}

function starter(found: RunStarter | undefined): string {
  if (!found) return 'unknown'
  const name = found.name ? `“${found.name}”` : '(deleted)'
  return found.kind === 'person' ? (found.name ?? 'a deleted account') : found.kind === 'ci-token' ? `CI token ${name}` : `agent token ${name}`
}

/**
 * Indices of the runs whose result is failed while the run before it, in time, passed. The
 * list is newest first, so that run is the next one in the list. The last loaded run has no
 * known predecessor until the next page loads.
 */
function regressions(runs: Pick<VerificationRun, 'result'>[]): Set<number> {
  const marked = new Set<number>()
  for (let i = 0; i < runs.length - 1; i++) if (runs[i].result === 'failed' && runs[i + 1].result === 'passed') marked.add(i)
  return marked
}

/**
 * Every run of one obligation, newest first: the evidence trail of what was checked, on what,
 * where, when and by whom. A change from passed to failed is marked.
 */
export function RunHistory({ projectId, promiseId, obligationId, nameOf }: Props) {
  const [open, setOpen] = useState(false)
  const runs = useInfiniteQuery({
    queryKey: ['projects', projectId, 'promises', promiseId, 'runs', obligationId],
    queryFn: ({ pageParam }) => promisesApi.runs(projectId, promiseId, obligationId, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.cursor,
    enabled: open,
  })
  const items = runs.data?.pages.flatMap((page) => page.items) ?? []
  const starters = Object.assign({}, ...(runs.data?.pages.map((page) => page.startedBy) ?? [])) as Record<string, RunStarter>
  const marked = regressions(items)

  const model = (run: VerificationRun) => {
    if (!run.modelRevision) return null
    const stream = nameOf(run.modelRevision.streamId) ?? 'a model'
    return `${stream}, version ${run.modelRevision.version}${run.modelRevision.revisionName ? `, revision “${run.modelRevision.revisionName}”` : ''}`
  }
  const source = (run: VerificationRun) => {
    if (!run.commitSha && !run.scanId) return null
    const scan = run.scanId ? nameOf(run.scanId) : undefined
    return `${run.commitSha ? `commit ${run.commitSha.slice(0, 10)}` : ''}${run.inputs?.ref ? ` of ${run.inputs.ref}` : ''}${run.inputs?.scanKind ? `, a ${run.inputs.scanKind} scan` : ''}${scan && !run.commitSha ? `scan of ${scan}` : ''}`
  }

  return (
    <details className="mt-1.5 text-xs" onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="cursor-pointer rounded text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">History</summary>
      {runs.isPending && open && <p className="mt-1 text-muted-foreground">Loading runs…</p>}
      {runs.isError && <p role="alert" className="mt-1 text-destructive">{runs.error.message}</p>}
      {runs.isSuccess && items.length === 0 && <p className="mt-1 text-muted-foreground">This obligation has no run.</p>}
      {items.length > 0 && (
        <ol className="mt-1 divide-y divide-border rounded-md border border-border" aria-label={`Runs of ${obligationId}, newest first`}>
          {items.map((run, i) => (
            <li key={run.id} className="px-2 py-1.5">
              <p className="flex flex-wrap items-baseline gap-x-2">
                <span className="font-medium" style={{ color: RESULT_COLOR[run.result] }}>{RESULT_LABEL[run.result]}</span>
                {marked.has(i) && (
                  <span className="rounded-full border border-[var(--trace-failure)] px-1.5 text-[10px] text-[var(--trace-failure)]" title="The run before this one passed.">
                    Passed → Failed
                  </span>
                )}
                {run.draft && <span className="rounded-full border border-border px-1.5 text-[10px] text-muted-foreground" title="The revision was a draft. This run does not count after approval.">draft</span>}
                <span className="text-muted-foreground">revision {run.promiseRevision}</span>
                <span className="ml-auto font-mono text-[10px] text-muted-foreground">{new Date(run.at).toLocaleString()}</span>
              </p>
              <p className="mt-0.5 break-words text-[11px] text-muted-foreground">
                {[model(run), source(run), run.environment && `against ${run.environment}`].filter(Boolean).join(' · ')}
              </p>
              <p className="text-[11px] text-muted-foreground">Started by {starter(starters[runActor(run)])}</p>
            </li>
          ))}
        </ol>
      )}
      {runs.hasNextPage && (
        <Button variant="ghost" size="sm" className="mt-1 h-6 text-xs" disabled={runs.isFetchingNextPage} onClick={() => runs.fetchNextPage()}>
          {runs.isFetchingNextPage ? 'Loading…' : 'Show more'}
        </Button>
      )}
    </details>
  )
}
