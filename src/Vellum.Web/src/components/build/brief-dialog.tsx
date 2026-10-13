import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CircleCheck, CircleX, Copy, Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { promisesApi, type BriefScope } from '@/lib/promises-api'

interface Props {
  projectId: string
  scope: BriefScope
  /** What the brief is for, in a few words: "ORD-014", "the change of remove-outbox". */
  subject: string
  fileName: string
  onClose: () => void
}

const count = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/**
 * The change-scoped brief for a coding agent, before it leaves Vellum: whether its scope
 * is build ready and why not, what it holds, and the text itself to copy or to download.
 */
export function BriefDialog({ projectId, scope, subject, fileName, onClose }: Props) {
  // A brief is made from the model as it is now, so it is never taken from a cache.
  const brief = useQuery({
    queryKey: ['projects', projectId, 'build', 'brief', scope],
    queryFn: () => promisesApi.brief(projectId, scope),
    staleTime: 0, gcTime: 0, retry: false,
  })
  const [copied, setCopied] = useState<'done' | 'failed' | null>(null)
  const data = brief.data
  const manifest = data?.manifest

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(data!.brief)
      setCopied('done')
    } catch {
      setCopied('failed')
    }
  }
  const download = () => {
    const url = URL.createObjectURL(new Blob([data!.brief], { type: 'text/markdown' }))
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader className="pr-8">
          <DialogTitle>Brief for {subject}</DialogTitle>
          <DialogDescription>
            The part of the model that this scope needs: approved facts, contracts, linked promises, required checks and open blockers. An agent can record evidence and propose a change. It cannot change or approve a promise.
          </DialogDescription>
        </DialogHeader>

        {brief.isLoading && <p className="text-sm text-muted-foreground">Making the brief…</p>}
        {brief.isError && <p role="alert" className="text-sm text-destructive">{brief.error.message}</p>}

        {data && manifest && (
          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
            <div className="rounded-md border border-border p-3">
              <p className="flex items-center gap-1.5 text-sm font-medium" style={{ color: manifest.buildReady ? 'var(--trace-request)' : 'var(--trace-retry)' }}>
                {manifest.buildReady ? <CircleCheck className="h-4 w-4" aria-hidden="true" /> : <CircleX className="h-4 w-4" aria-hidden="true" />}
                {manifest.buildReady ? 'This scope is build ready' : `This scope is not build ready: ${count(manifest.blockers.length, 'blocker')}`}
              </p>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                {manifest.buildReady
                  ? 'No blocker is open for anything in this brief.'
                  : 'You can still give the brief to an agent. It lists each blocker, so the agent does not treat an open point as settled.'}
              </p>
              {manifest.blockers.length > 0 && (
                <ul className="mt-2 space-y-1.5" aria-label="Blockers in this scope">
                  {manifest.blockers.map((blocker, i) => (
                    <li key={i} className="rounded-md border border-border px-2.5 py-1.5">
                      <p className="font-mono text-[10px] text-[var(--trace-retry)]">{blocker.code}</p>
                      <p className="break-words text-xs text-foreground">{blocker.message}</p>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            <section aria-labelledby="brief-holds" className="text-xs">
              <h3 id="brief-holds" className="font-medium uppercase tracking-wider text-muted-foreground">In the brief</h3>
              <p className="mt-1 text-foreground">
                {manifest.included.requirementIds.length > 0
                  ? <>Requirements: <span className="font-mono">{manifest.included.requirementIds.join(', ')}</span>.</>
                  : 'No approved promise is in this scope, so the brief holds no requirement.'}
              </p>
              <p className="mt-0.5 text-muted-foreground">
                {[
                  count(manifest.included.elementIds.length, 'element'), count(manifest.included.operationIds.length, 'operation'),
                  count(manifest.included.channelIds.length, 'channel'), count(manifest.included.subscriptionIds.length, 'subscription'),
                  count(manifest.included.messageIds.length, 'message'), count(manifest.included.schemaIds.length, 'schema'),
                  count(manifest.included.scenarioIds.length, 'scenario'),
                ].join(', ')}.
              </p>
              {manifest.changeScanId && <p className="mt-0.5 text-muted-foreground">Section 8 holds the change impact of the scan, each entry with its certainty.</p>}
              {manifest.notApproved.length > 0 && (
                <p className="mt-0.5 text-[var(--trace-retry)]">
                  Not approved, so not a requirement: <span className="font-mono">{manifest.notApproved.join(', ')}</span>. The brief lists {manifest.notApproved.length === 1 ? 'it' : 'them'} under “do not implement”.
                </p>
              )}
              {manifest.retired.length > 0 && <p className="mt-0.5 text-muted-foreground">Retired: <span className="font-mono">{manifest.retired.join(', ')}</span>.</p>}
              {manifest.unconfirmedFacts.length > 0 && (
                <p className="mt-0.5 text-muted-foreground">
                  {count(manifest.unconfirmedFacts.length, 'fact')} in scope {manifest.unconfirmedFacts.length === 1 ? 'is' : 'are'} inferred, unknown or stale. The brief leaves {manifest.unconfirmedFacts.length === 1 ? 'it' : 'them'} out of the approved facts.
                </p>
              )}
              <p className="mt-1 font-mono text-[10px] text-muted-foreground">
                {manifest.format} · model version {manifest.pinned.streamVersion} · {manifest.bytes} bytes · sha256 {manifest.sha256.slice(0, 12)}
              </p>
            </section>

            <section aria-labelledby="brief-text">
              <h3 id="brief-text" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Text of the brief</h3>
              {/* The brief holds names and text of the model and of repositories. It is shown as text and never rendered. */}
              <pre tabIndex={0} aria-labelledby="brief-text" className="mt-1.5 max-h-[45vh] overflow-auto whitespace-pre-wrap break-words rounded-md border border-border bg-background p-3 font-mono text-[11px] leading-relaxed text-foreground focus-visible:outline-2 focus-visible:outline-primary">
                {data.brief}
              </pre>
            </section>
          </div>
        )}

        <div className="flex flex-wrap items-center gap-2">
          <p className="min-w-0 flex-1 text-xs text-muted-foreground" role="status">
            {copied === 'done' ? 'The brief is on the clipboard.' : copied === 'failed' ? 'The browser did not allow the copy. Download the file.' : ''}
          </p>
          <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
          <Button variant="secondary" size="sm" className="gap-1.5" disabled={!data} onClick={() => void copy()}><Copy className="h-3.5 w-3.5" /> Copy</Button>
          <Button size="sm" className="gap-1.5" disabled={!data} onClick={download}><Download className="h-3.5 w-3.5" /> Download {fileName}</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
