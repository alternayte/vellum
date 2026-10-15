import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Lock } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { KIND_INFO, isGitHost, sourcesApi, type BulkAddItem, type GitHostKind } from '@/lib/import-api'
import { sourcesKey } from '@/hooks/use-sources'

/** The most repositories one bulk add takes. */
const MAX_REPOSITORIES = 100

const labelClass = 'block text-xs font-medium text-muted-foreground'

/**
 * Adds many repositories of one organization, group or workspace: one Source and one scan
 * each. The scans wait in the scan queue, and each Source reports its own progress.
 */
export function BulkAddDialog({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const kinds = useQuery({ queryKey: ['projects', projectId, 'sources', 'kinds'], queryFn: () => sourcesApi.kinds(projectId) })
  const hosts = (kinds.data?.kinds ?? []).filter((k): k is typeof k & { kind: GitHostKind } => isGitHost(k.kind))
  const [chosen, setChosen] = useState<GitHostKind | null>(null)
  const kind = chosen ?? hosts.find((h) => h.available)?.kind ?? null
  const host = hosts.find((h) => h.kind === kind)
  const [owner, setOwner] = useState('')
  const [listed, setListed] = useState<{ kind: GitHostKind; owner: string } | null>(null)
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [ref, setRef] = useState('')
  const [results, setResults] = useState<BulkAddItem[] | null>(null)

  const repositories = useQuery({
    queryKey: ['projects', projectId, 'sources', 'repositories', listed?.kind, listed?.owner, page],
    queryFn: () => sourcesApi.repositories(projectId, listed!.kind, listed!.owner, page),
    enabled: listed !== null,
    retry: false,
    placeholderData: (previous) => previous,
  })

  const add = useMutation({
    mutationFn: () => sourcesApi.bulk(projectId, { kind: listed!.kind, repositories: [...selected], ref: ref.trim() || undefined }),
    onSuccess: (data) => {
      setResults(data.items)
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
        queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
      ])
    },
    onError: shownInline,
  })

  const list = (e: FormEvent) => {
    e.preventDefault()
    if (!kind || !owner.trim()) return
    setListed({ kind, owner: owner.trim() })
    setPage(1)
    setSelected(new Set())
  }
  const toggle = (repository: string) => setSelected((current) => {
    const next = new Set(current)
    if (next.has(repository)) next.delete(repository)
    else next.add(repository)
    return next
  })
  const shown = repositories.data?.repositories ?? []
  const choosable = shown.filter((r) => r.sourceId === null)
  const allOnPage = choosable.length > 0 && choosable.every((r) => selected.has(r.repository))
  const toggleAll = () => setSelected((current) => {
    const next = new Set(current)
    for (const r of choosable) {
      if (allOnPage) next.delete(r.repository)
      else next.add(r.repository)
    }
    return next
  })

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !add.isPending) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Add many repositories</DialogTitle>
          <DialogDescription>
            Each repository becomes a Source with one scan. The scans wait in a queue and run a few at a time for each host.
          </DialogDescription>
        </DialogHeader>

        {kinds.isLoading && <p className="text-sm text-muted-foreground">Loading the git hosts…</p>}
        {kinds.isError && <p role="alert" className="text-sm text-destructive">{kinds.error.message}</p>}

        {results ? (
          <BulkResults results={results} onClose={onClose} />
        ) : kinds.data && (
          <>
            <form onSubmit={list} className="grid items-end gap-2 sm:grid-cols-[10rem_minmax(0,1fr)_auto]">
              <label className={labelClass}>
                Host
                <select
                  className={cn(selectClass, 'mt-1')} value={kind ?? ''}
                  onChange={(e) => { setChosen(e.target.value as GitHostKind); setListed(null); setSelected(new Set()) }}
                >
                  {hosts.map((h) => (
                    <option key={h.kind} value={h.kind} disabled={!h.available}>
                      {KIND_INFO[h.kind].label}{h.available ? '' : ' (not set up)'}
                    </option>
                  ))}
                </select>
              </label>
              <label className={labelClass}>
                {kind ? KIND_INFO[kind].owner : 'Owner'}
                <Input className="mt-1" value={owner} onChange={(e) => setOwner(e.target.value)} placeholder={kind ? KIND_INFO[kind].placeholder : ''} spellCheck={false} data-autofocus />
              </label>
              <Button type="submit" variant="secondary" disabled={!host?.available || !owner.trim() || repositories.isFetching}>
                {repositories.isFetching && listed ? 'Listing…' : 'List repositories'}
              </Button>
            </form>
            {host?.where && <p className="-mt-1 text-[11px] text-muted-foreground">On {host.where}.</p>}
            {hosts.filter((h) => !h.available).map((h) => (
              <p key={h.kind} className="-mt-1 text-[11px] text-muted-foreground"><span className="text-foreground">{KIND_INFO[h.kind].label}:</span> {h.reason}</p>
            ))}

            {repositories.isError && <p role="alert" className="text-sm text-destructive">{repositories.error.message}</p>}

            {repositories.data && listed && (
              <div className="flex min-h-0 flex-1 flex-col rounded-lg border border-border">
                <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-1.5 text-xs">
                  <label className="flex items-center gap-2 text-foreground">
                    <input
                      type="checkbox" className="h-3.5 w-3.5 accent-[var(--primary)]"
                      checked={allOnPage} disabled={choosable.length === 0} onChange={toggleAll}
                    />
                    Select all on this page
                  </label>
                  <span className="flex-1 text-muted-foreground">{selected.size} selected</span>
                  <span className="text-muted-foreground">Page {page}</span>
                  <Button type="button" size="xs" variant="ghost" disabled={page === 1 || repositories.isFetching} onClick={() => setPage(page - 1)}>Previous</Button>
                  <Button type="button" size="xs" variant="ghost" disabled={!repositories.data.hasMore || repositories.isFetching} onClick={() => setPage(page + 1)}>Next</Button>
                </div>
                {shown.length === 0 && <p className="px-3 py-3 text-sm text-muted-foreground">This page lists no repository.</p>}
                <ul className="min-h-0 flex-1 overflow-y-auto">
                  {shown.map((r) => (
                    <li key={r.repository} className="border-b border-border last:border-b-0">
                      <label className="flex items-center gap-2 px-3 py-1.5 text-sm">
                        <input type="checkbox" className="h-3.5 w-3.5 shrink-0 accent-[var(--primary)]" checked={selected.has(r.repository)} onChange={() => toggle(r.repository)} />
                        <span className="min-w-0 flex-1 truncate font-mono text-xs text-foreground" title={r.name}>{r.repository}</span>
                        {r.isPrivate && <Lock className="h-3 w-3 shrink-0 text-muted-foreground" aria-label="Private" />}
                        <span className="hidden shrink-0 font-mono text-[10px] text-muted-foreground sm:inline">{r.defaultBranch}</span>
                        {r.sourceId && (
                          <span className="shrink-0 rounded-full border border-border px-1.5 text-[10px] text-muted-foreground" title="Selecting it queues a new scan of that Source">Has a Source</span>
                        )}
                      </label>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {listed && repositories.data && (
              <label className={labelClass}>
                Branch or tag for every scan
                <Input className="mt-1" value={ref} onChange={(e) => setRef(e.target.value)} placeholder="The default branch of each repository" spellCheck={false} />
              </label>
            )}
            {selected.size > MAX_REPOSITORIES && (
              <p role="alert" className="text-xs text-destructive">Choose at most {MAX_REPOSITORIES} repositories at a time. {selected.size} are selected.</p>
            )}
            {add.isError && <p role="alert" className="text-xs text-destructive">{add.error.message}</p>}

            <DialogFooter>
              <Button type="button" variant="ghost" disabled={add.isPending} onClick={onClose}>Cancel</Button>
              <Button type="button" disabled={selected.size === 0 || selected.size > MAX_REPOSITORIES || add.isPending} onClick={() => add.mutate()}>
                {add.isPending ? 'Adding…' : `Add ${selected.size} and scan`}
              </Button>
            </DialogFooter>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

/** One line for each repository: added or already there, and whether its scan was queued. */
function BulkResults({ results, onClose }: { results: BulkAddItem[]; onClose: () => void }) {
  const queued = results.filter((r) => r.scanId).length
  const failed = results.length - queued
  return (
    <>
      <p role="status" className="text-sm text-foreground">
        {queued} {queued === 1 ? 'scan' : 'scans'} queued{failed > 0 ? `, ${failed} not` : ''}.
      </p>
      <ul className="min-h-0 flex-1 overflow-y-auto rounded-lg border border-border">
        {results.map((r) => (
          <li key={r.repository} className="border-b border-border px-3 py-1.5 last:border-b-0">
            <div className="flex items-baseline gap-2">
              <span className="min-w-0 flex-1 truncate font-mono text-xs text-foreground">{r.repository}</span>
              <span className="shrink-0 text-[11px] text-muted-foreground">{r.sourceId ? (r.created ? 'Added' : 'Already a Source') : 'Not added'}</span>
              <span className={cn('shrink-0 text-[11px]', r.scanId ? 'text-[var(--trace-request)]' : 'text-[var(--trace-failure)]')}>
                {r.scanId ? 'Scan queued' : 'No scan'}
              </span>
            </div>
            {r.error && <p className="mt-0.5 text-xs text-[var(--trace-failure)]">{r.error.title}{r.error.detail ? ` ${r.error.detail}` : ''}</p>}
          </li>
        ))}
      </ul>
      <DialogFooter>
        <Button type="button" onClick={onClose} data-autofocus>Done</Button>
      </DialogFooter>
    </>
  )
}
