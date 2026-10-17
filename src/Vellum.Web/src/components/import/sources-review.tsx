import { useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Activity, FileArchive, FileText, Folder, GitBranch, PlugZap, Radar, RotateCw, Trash2, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ConfirmDialog } from '@/components/shell/confirm-dialog'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import {
  ARCHIVE_ACCEPT, CONTENT_NOT_HELD, describeReview, describeScan, isActive, revisionLabel, sourceState, sourcesApi,
  type ScanStatus, type ScanSummary, type SourceSnapshot, type SourceSummary,
} from '@/lib/import-api'
import { sourcesKey, useSources } from '@/hooks/use-sources'
import { UnconfirmedDependencies } from './source-facts'
import { useArchiveUpload } from '@/hooks/use-archive-upload'
import { UploadProgress } from './archive-upload'
import { FolderPickerButton, FolderUploadProgress } from './folder-picker'
import { useFolderUpload } from '@/hooks/use-folder-upload'

/** An uploaded Markdown or text document that is not an ADR: only the AI path reads it. */
function isProse(path: string): boolean {
  return /\.(md|markdown|txt)$/i.test(path) && !/(^|\/)(docs?\/adr|docs\/decisions)\/[^/]+$/i.test(path)
}

function short(value: string | null | undefined, length = 10): string {
  return value ? value.slice(0, length) : ''
}

const STATUS_COLOR: Record<ScanStatus, string> = {
  queued: 'var(--muted-foreground)',
  running: 'var(--trace-data)',
  waiting: 'var(--trace-retry)',
  completed: 'var(--trace-request)',
  failed: 'var(--trace-failure)',
  cancelled: 'var(--muted-foreground)',
}

const KIND_ICON: Record<string, typeof GitBranch> = { directory: Folder, archive: FileArchive, traces: Activity, newrelic: Radar, 'model-file': FileText }

/** What "scan again" does for each kind, as the button says it. */
function rescan(kind: string): { label: string; title: string } {
  if (kind === 'directory') return { label: 'Read again', title: 'Read the folder again as it is now. Each read is a new Snapshot.' }
  if (kind === 'archive') return { label: 'Upload again', title: 'Upload a new .zip or .tar.gz. Each upload is a new Snapshot.' }
  if (kind === 'newrelic') return { label: 'Read again', title: 'Read the account again' }
  return { label: 'Scan again', title: 'Scan the default ref of this Source again' }
}

/** The list of Sources: each with its newest scan, its newest Snapshot and how much of it needs a person. */
export function SourcesList({ projectId, onProse }: {
  projectId: string
  /** Opens the prose import for a document of the Source. */
  onProse?: (source: SourceSummary) => void
}) {
  const queryClient = useQueryClient()
  const sources = useSources(projectId)
  const [deleting, setDeleting] = useState<SourceSummary | null>(null)
  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
    queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
  ])
  const scan = useMutation({
    mutationFn: (id: string) => sourcesApi.scan(projectId, id, {}),
    onSuccess: refresh,
    onError: shownInline,
  })
  const remove = useMutation({
    mutationFn: (id: string) => sourcesApi.remove(projectId, id),
    onSuccess: () => { setDeleting(null); return refresh() },
    onError: shownInline,
  })
  const { upload, sent } = useArchiveUpload(projectId)
  // A Source made from a folder in the browser is read again by choosing the folder again.
  const folder = useFolderUpload(projectId)

  if (sources.isLoading) return <p className="px-3 py-2 text-xs text-muted-foreground">Loading Sources…</p>
  if (sources.isError) return <p role="alert" className="px-3 py-2 text-xs text-destructive">{sources.error.message}</p>
  const list = sources.data ?? []
  if (list.length === 0) return <p className="px-3 py-2 text-xs text-muted-foreground">No Source yet. Add a repository, a folder, an archive or a runtime Source.</p>

  return (
    <div>
      {scan.isError && <p role="alert" className="px-3 pb-1 text-xs text-[var(--trace-failure)]">{scan.error.message}</p>}
      <ul className="space-y-1 px-2 pb-2">
        {list.map((source) => {
          const Icon = KIND_ICON[source.kind] ?? GitBranch
          const busy = source.latestScan !== null && isActive(source.latestScan.status)
          const uploading = upload.isPending && upload.variables?.sourceId === source.id
          const uploadFailed = upload.isError && upload.variables?.sourceId === source.id
          const folderBusy = folder.run.isPending && folder.run.variables?.sourceId === source.id
          const folderFailed = folder.run.isError && folder.run.variables?.sourceId === source.id
          const again = rescan(source.kind)
          return (
            <li key={source.id} className="rounded-md border border-border p-2">
              <div className="flex items-start gap-2">
                <Icon className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                <div className="min-w-0 flex-1">
                  <p className="flex items-baseline gap-1.5">
                    <span className="truncate text-sm text-foreground">{source.name}</span>
                    <span className="shrink-0 rounded-full border border-border px-1.5 font-mono text-[10px] text-muted-foreground">{source.kind}</span>
                  </p>
                  <p className="truncate font-mono text-[10px] text-muted-foreground" title={source.citation}>{source.citation}</p>
                  {source.latestSnapshot && <SnapshotLine source={source} snapshot={source.latestSnapshot} />}
                  {source.latestSnapshot && !source.latestSnapshot.contentHeld && (
                    <p className="mt-1 flex items-start gap-1.5 text-[11px] text-muted-foreground" title={CONTENT_NOT_HELD}>
                      <span className="shrink-0 rounded-full border border-[var(--trace-retry)]/60 px-1.5 text-[10px] text-[var(--trace-retry)]">content not held</span>
                      <span>Facts and citations only. Source lines stay on the machine that pushed.</span>
                    </p>
                  )}
                  {source.connection && <RuntimeStatus source={source} />}
                  {source.latestScan && !source.connection && <ScanState scan={source.latestScan} />}
                  <p className={cn('mt-1 text-xs', source.review.pending > 0 ? 'text-foreground' : 'text-muted-foreground')}>{describeReview(source)}</p>
                  {(source.disagreements?.length ?? 0) > 0 && (
                    <details className="mt-1 text-xs">
                      <summary className="cursor-pointer text-muted-foreground hover:text-foreground">Dependencies another Source does not see</summary>
                      <div className="mt-1"><UnconfirmedDependencies items={source.disagreements!} /></div>
                    </details>
                  )}
                </div>
              </div>
              {uploading && <div className="mt-2"><UploadProgress sent={sent} pending /></div>}
              {uploadFailed && <p role="alert" className="mt-1.5 text-xs text-[var(--trace-failure)]">{upload.error.message}</p>}
              {folderBusy && <div className="mt-2"><FolderUploadProgress packing={folder.packing} sent={folder.sent} uploading={folder.uploading} /></div>}
              {folderFailed && <p role="alert" className="mt-1.5 text-xs text-[var(--trace-failure)]">{folder.run.error?.message}</p>}
              <div className="mt-2 flex flex-wrap gap-1.5">
                {source.kind === 'agent-push' ? (
                  <span className="self-center text-[11px] text-muted-foreground">Vellum does not hold these files. Run vellum scan again to update.</span>
                ) : source.kind === 'model-file' ? (
                  <span className="self-center text-[11px] text-muted-foreground">Upload the file again for a new Snapshot.</span>
                ) : source.kind === 'traces' ? (
                  <TraceUploadButton projectId={projectId} source={source} />
                ) : source.kind === 'archive' ? (
                  <>
                  <FolderPickerButton
                    onPick={(picked) => folder.run.mutate({ picked, sourceId: source.id })}
                    disabled={busy || upload.isPending || folder.run.isPending}
                    className="inline-flex h-6 items-center gap-1 rounded-md px-2 text-xs font-medium text-foreground hover:bg-muted"
                  >
                    <span title="Choose the folder again. The browser reads it as it is now, and each read is a new Snapshot." className="inline-flex items-center gap-1">
                      <Folder className="h-3 w-3" aria-hidden="true" /> Choose folder again
                    </span>
                  </FolderPickerButton>
                  <label
                    title={again.title}
                    className={cn(
                      'inline-flex h-6 cursor-pointer items-center gap-1 rounded-md px-2 text-xs font-medium text-foreground hover:bg-muted focus-within:outline-2 focus-within:outline-primary',
                      (busy || upload.isPending) && 'pointer-events-none opacity-50',
                    )}
                  >
                    <Upload className="h-3 w-3" aria-hidden="true" /> {again.label}
                    <input
                      type="file" accept={ARCHIVE_ACCEPT} className="sr-only" disabled={busy || upload.isPending}
                      onChange={(e) => {
                        const file = e.target.files?.[0]
                        e.target.value = ''
                        if (file) upload.mutate({ sourceId: source.id, file })
                      }}
                    />
                  </label>
                  </>
                ) : (
                  <Button
                    size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs"
                    disabled={scan.isPending || busy}
                    title={again.title}
                    onClick={() => scan.mutate(source.id)}
                  >
                    <RotateCw className="h-3 w-3" /> {again.label}
                  </Button>
                )}
                {onProse && source.latestSnapshot && !['agent-push', 'traces', 'newrelic'].includes(source.kind)
                  && (source.kind !== 'model-file' || isProse(source.locator)) && (
                  <Button
                    size="sm" variant="ghost" className="h-6 gap-1 px-2 text-xs"
                    title="Ask the AI to propose what a README or design document of this Source describes"
                    onClick={() => onProse(source)}
                  >
                    <FileText className="h-3 w-3" /> Propose from a document
                  </Button>
                )}
                {source.kind === 'newrelic' && <TestConnectionButton projectId={projectId} source={source} />}
                {source.kind === 'newrelic' && <ReplaceKey projectId={projectId} source={source} />}
                <Button
                  size="sm" variant="ghost" className="ml-auto h-6 gap-1 px-2 text-xs text-muted-foreground hover:text-destructive"
                  title="Delete this Source and its Snapshots"
                  onClick={() => { remove.reset(); setDeleting(source) }}
                >
                  <Trash2 className="h-3 w-3" /> Delete
                </Button>
              </div>
            </li>
          )
        })}
      </ul>
      {deleting && (
        <ConfirmDialog
          title="Delete Source"
          description={<>“{deleting.name}” is deleted with its scans, its Snapshots and the stored files that no other Source needs. The model keeps every fact that people accepted from it.</>}
          confirmLabel="Delete Source"
          destructive
          pending={remove.isPending}
          error={remove.error?.message}
          onConfirm={() => remove.mutate(deleting.id)}
          onClose={() => setDeleting(null)}
        />
      )}
    </div>
  )
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
 * The newest Snapshot. A git Source cites a commit; a folder or an archive cites the Snapshot
 * ID. The commit that a folder's `.git/HEAD` names is shown as metadata only.
 */
function SnapshotLine({ source, snapshot }: { source: SourceSummary; snapshot: SourceSnapshot }) {
  const git = revisionLabel(source.kind) === 'commit'
  return (
    <p className="font-mono text-[10px] text-muted-foreground" title={`Snapshot ${snapshot.snapshotId}${snapshot.commitSha ? `\nCommit ${snapshot.commitSha}` : ''}`}>
      {git
        ? <>commit {short(snapshot.revision)} · Snapshot {short(snapshot.snapshotId, 8)}</>
        : <>Snapshot {short(snapshot.snapshotId, 12)}{snapshot.reportedCommitSha && <> · .git/HEAD {short(snapshot.reportedCommitSha)}</>}</>}
      {` · ${snapshot.files} ${snapshot.files === 1 ? 'file' : 'files'}`}
      {!snapshot.isComplete && ' · partial'}
    </p>
  )
}

/** The state of the newest scan: progress while it runs, the failure with its code, the retry time while it waits. */
function ScanState({ scan }: { scan: ScanSummary }) {
  const p = scan.progress
  const text =
    scan.status === 'running' && p.filesListed > 0 ? `Reading files: ${p.filesRead} of ${p.filesListed}${p.filesSkipped > 0 ? `, ${p.filesSkipped} skipped` : ''}`
    : scan.status === 'completed' && scan.isComplete ? `Scan complete: ${p.filesRead} of ${p.filesListed} files read${p.filesSkipped > 0 ? `, ${p.filesSkipped} skipped` : ''}`
    : describeScan(scan)
  return (
    <p className="mt-1 flex items-start gap-1.5 text-xs" style={{ color: STATUS_COLOR[scan.status] }}>
      <span className={cn('mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-current', scan.status === 'running' && 'motion-safe:animate-pulse')} aria-hidden="true" />
      <span className={cn('min-w-0', scan.status === 'completed' && scan.isComplete && 'text-muted-foreground')}>
        {text}
        {scan.error && <span className="ml-1 font-mono text-[10px] text-muted-foreground">({scan.error.code})</span>}
      </span>
    </p>
  )
}
