import { useQuery, useMutation } from '@tanstack/react-query'
import { CircleCheck, CircleX, Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { downloadBuild, promisesApi } from '@/lib/promises-api'

/**
 * The handoff to an implementation agent: a standalone SDD and a bundle of contracts,
 * scenarios and promises, generated from the model as it is now. The same model always
 * gives the same files and the same hashes.
 */
export function BuildFlyout({ projectId }: { projectId: string }) {
  const manifest = useQuery({ queryKey: ['projects', projectId, 'build', 'manifest'], queryFn: () => promisesApi.manifest(projectId), staleTime: 0 })
  const download = useMutation({ mutationFn: (format: 'sdd' | 'bundle') => downloadBuild(projectId, format), onError: shownInline })
  const data = manifest.data

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Build</span>
      </div>
      <div className="flex-1 space-y-3 overflow-y-auto p-3">
        {manifest.isLoading && <p className="text-xs text-muted-foreground">Checking the model…</p>}
        {manifest.isError && <p role="alert" className="text-xs text-destructive">{manifest.error.message}</p>}
        {data && (
          <>
            <div className="rounded-md border border-border p-3">
              <p className="flex items-center gap-1.5 text-sm font-medium" style={{ color: data.buildReady ? 'var(--trace-request)' : 'var(--trace-retry)' }}>
                {data.buildReady ? <CircleCheck className="h-4 w-4" /> : <CircleX className="h-4 w-4" />}
                {data.buildReady ? 'Build ready' : `Not build ready: ${data.blockers.length} ${data.blockers.length === 1 ? 'blocker' : 'blockers'}`}
              </p>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                {data.buildReady
                  ? 'The export has no open blocker.'
                  : 'You can still export. The SDD lists every blocker, so an agent does not treat an open point as settled.'}
              </p>
              <p className="mt-2 font-mono text-[10px] text-muted-foreground">model version {data.pinned.streamVersion} · bundle {data.bundleHash.slice(0, 12)}</p>
            </div>

            <div className="flex gap-2">
              <Button size="sm" className="flex-1 gap-1.5" disabled={download.isPending} onClick={() => download.mutate('sdd')}><Download className="h-3.5 w-3.5" /> SDD.md</Button>
              <Button size="sm" variant="secondary" className="flex-1 gap-1.5" disabled={download.isPending} onClick={() => download.mutate('bundle')}><Download className="h-3.5 w-3.5" /> Bundle</Button>
            </div>
            {download.isError && <p role="alert" className="text-xs text-destructive">{download.error.message}</p>}

            {data.blockers.length > 0 && (
              <section aria-labelledby="build-blockers">
                <h3 id="build-blockers" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Blockers</h3>
                <ul className="mt-1.5 space-y-1.5">
                  {data.blockers.map((blocker, i) => (
                    <li key={i} className="rounded-md border border-border px-2.5 py-1.5">
                      <p className="font-mono text-[10px] text-[var(--trace-retry)]">{blocker.code}</p>
                      <p className="text-xs text-foreground">{blocker.message}</p>
                    </li>
                  ))}
                </ul>
              </section>
            )}

            <section aria-labelledby="build-files">
              <h3 id="build-files" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Files in the bundle <span className="font-mono">{data.files.length}</span></h3>
              <ul className="mt-1.5 space-y-0.5">
                {data.files.map((file) => (
                  <li key={file.path} className="flex items-baseline gap-2 font-mono text-[11px]">
                    <span className="min-w-0 flex-1 truncate text-foreground" title={file.path}>{file.path}</span>
                    <span className="shrink-0 text-muted-foreground" title={file.sha256}>{file.sha256.slice(0, 8)}</span>
                  </li>
                ))}
              </ul>
              {!data.selfCheck.passed && (
                <p role="status" className="mt-2 text-xs text-[var(--trace-failure)]">
                  The generated contracts do not read back as the model. {data.selfCheck.notes[0]}
                </p>
              )}
            </section>
          </>
        )}
      </div>
    </div>
  )
}
