import { useRef, useState } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { CircleCheck, CircleX, Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { selectClass } from '@/components/settings/fields'
import { cn } from '@/lib/utils'
import { importApi } from '@/lib/import-api'
import { downloadBuild, promisesApi, verifyBundle, type BriefScope } from '@/lib/promises-api'
import { BriefDialog } from './brief-dialog'

type ScopeKind = 'promise' | 'change' | 'elements'

const SCOPES: { kind: ScopeKind; label: string }[] = [
  { kind: 'promise', label: 'A promise' },
  { kind: 'change', label: 'A change' },
  { kind: 'elements', label: 'Elements' },
]

/**
 * The handoff to an implementation agent: a standalone SDD and a bundle of contracts,
 * scenarios and promises, generated from the model as it is now. The same model always
 * gives the same files and the same hashes.
 */
export function BuildFlyout({ projectId, elements, selection }: {
  projectId: string
  elements: { id: string; name: string }[]
  /** The elements selected on the canvas when the flyout opens. They start the element scope of a brief. */
  selection: () => string[]
}) {
  const manifest = useQuery({ queryKey: ['projects', projectId, 'build', 'manifest'], queryFn: () => promisesApi.manifest(projectId), staleTime: 0 })
  const download = useMutation({ mutationFn: (format: 'sdd' | 'bundle') => downloadBuild(projectId, format), onError: shownInline })
  const verify = useMutation({ mutationFn: (file: File) => verifyBundle(projectId, file), onError: shownInline })
  const picker = useRef<HTMLInputElement>(null)
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

            <BriefScopeForm projectId={projectId} elements={elements} selection={selection} />

            {data.blockers.length > 0 && (
              <section aria-labelledby="build-blockers">
                <h3 id="build-blockers" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Blockers</h3>
                <ul className="mt-1.5 space-y-1.5">
                  {data.blockers.map((blocker, i) => (
                    <li key={i} className="rounded-md border border-border px-2.5 py-1.5">
                      <p className="font-mono text-[10px] text-[var(--trace-retry)]">{blocker.code}</p>
                      <p className="break-words text-xs text-foreground">{blocker.message}</p>
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

            <section aria-labelledby="build-verify">
              <h3 id="build-verify" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Check a bundle</h3>
              <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                Vellum generates the bundle again from the versions that the file names, and compares every file.
              </p>
              <input
                ref={picker} type="file" accept=".zip,application/zip" className="sr-only" aria-label="Bundle file"
                onChange={(e) => { const file = e.target.files?.[0]; if (file) verify.mutate(file); e.target.value = '' }}
              />
              <Button size="sm" variant="secondary" className="mt-2 w-full" disabled={verify.isPending} onClick={() => picker.current?.click()}>
                {verify.isPending ? 'Checking…' : 'Select a bundle file'}
              </Button>
              {verify.isError && <p role="alert" className="mt-2 text-xs text-destructive">{verify.error.message}</p>}
              {verify.data && (
                <div role="status" className="mt-2 space-y-1 rounded-md border border-border p-2.5 text-xs">
                  <p className="font-medium" style={{ color: verify.data.matchesPinned ? 'var(--trace-request)' : 'var(--trace-failure)' }}>
                    {verify.data.matchesPinned
                      ? 'The files are what Vellum generates for the versions that the bundle names.'
                      : 'The bundle differs from what Vellum generates for the versions that it names.'}
                  </p>
                  {verify.data.matchesPinned && !verify.data.identical && (
                    <p className="text-muted-foreground">The archive was written again by another tool. The files in it are unchanged.</p>
                  )}
                  {verify.data.manifestDifferences.length > 0 && (
                    <p className="text-muted-foreground">Changed in the manifest: <span className="font-mono text-foreground">{verify.data.manifestDifferences.join(', ')}</span></p>
                  )}
                  {verify.data.files.filter((f) => f.status !== 'match').map((f) => (
                    <p key={f.path} className="font-mono text-[11px] text-[var(--trace-failure)]">{f.path} · {f.status}</p>
                  ))}
                  <p className="text-muted-foreground">
                    {verify.data.current
                      ? 'The model has not changed since this bundle was made.'
                      : `The model has changed since this bundle was made: ${verify.data.moved.length} ${verify.data.moved.length === 1 ? 'input' : 'inputs'} moved.`}
                  </p>
                  {verify.data.moved.slice(0, 8).map((m) => (
                    <p key={m.kind + m.id} className="font-mono text-[11px] text-muted-foreground">{m.kind} {m.id.slice(0, 8)} · {m.pinned ?? 'none'} → {m.current ?? 'none'}</p>
                  ))}
                  {!verify.data.issued && (
                    <p className="text-muted-foreground">The bundle has no seal of this Vellum instance, so Vellum does not export its state again. A bundle of an older format has no seal.</p>
                  )}
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </div>
  )
}

/**
 * A brief holds only the part of the model that one change needs. The scope is one
 * promise, the change of one scan, or a set of elements.
 */
function BriefScopeForm({ projectId, elements, selection }: { projectId: string; elements: { id: string; name: string }[]; selection: () => string[] }) {
  const promises = useQuery({ queryKey: ['projects', projectId, 'promises'], queryFn: () => promisesApi.list(projectId) })
  const scans = useQuery({ queryKey: ['projects', projectId, 'imports'], queryFn: () => importApi.list(projectId) })
  const completed = (scans.data ?? []).filter((s) => s.status === 'completed')
  const [kind, setKind] = useState<ScopeKind>('promise')
  const [promiseId, setPromiseId] = useState('')
  const [scanId, setScanId] = useState('')
  const [elementIds, setElementIds] = useState(() => selection().filter((id) => elements.some((e) => e.id === id)))
  const [preview, setPreview] = useState<{ scope: BriefScope; subject: string; fileName: string } | null>(null)

  // The first of each list stands until the person chooses another. The newest scan is first.
  const promise = promises.data?.find((p) => p.id === promiseId) ?? promises.data?.[0]
  const scan = completed.find((s) => s.id === scanId) ?? completed[0]
  const scanRef = scan ? (scan.ref ?? scan.trackedRef ?? 'default branch') : ''
  const chosen = kind === 'promise'
    ? promise && { scope: { promiseIds: [promise.id] }, subject: promise.requirementId, fileName: `brief-${promise.requirementId}.md` }
    : kind === 'change'
      ? scan && { scope: { changeScanId: scan.id }, subject: `the change of ${scanRef}`, fileName: `brief-${scan.repository.replace(/[^a-z0-9]+/gi, '-')}-${scan.commitSha?.slice(0, 10)}.md` }
      : elementIds.length > 0 && {
          scope: { elementIds }, fileName: 'brief-elements.md',
          subject: elementIds.length === 1 ? (elements.find((e) => e.id === elementIds[0])?.name ?? 'one element') : `${elementIds.length} elements`,
        }
  const missing = kind === 'promise'
    ? 'This project has no promise. Declare one in Promises.'
    : kind === 'change'
      ? 'This project has no completed scan. Scan a repository in Import from GitHub.'
      : 'Select at least one element.'

  return (
    <section aria-labelledby="build-brief">
      <h3 id="build-brief" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Brief for an agent</h3>
      <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
        A brief holds only what one change needs: approved facts, contracts, linked promises, required checks and blockers.
      </p>
      <fieldset className="mt-2">
        <legend className="text-[11px] font-medium text-muted-foreground">Scope</legend>
        <div className="mt-1 grid grid-cols-3 gap-1 rounded-lg border border-border p-0.5">
          {SCOPES.map((scope) => (
            <label key={scope.kind} className={cn('cursor-pointer rounded-md px-1.5 py-1 text-center text-xs has-focus-visible:outline-2 has-focus-visible:outline-primary', kind === scope.kind ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground')}>
              <input type="radio" name="brief-scope" className="sr-only" checked={kind === scope.kind} onChange={() => setKind(scope.kind)} />
              {scope.label}
            </label>
          ))}
        </div>
      </fieldset>

      {kind === 'promise' && promise && (
        <label className="mt-2 block text-[11px] font-medium text-muted-foreground">
          Promise
          <select className={cn(selectClass, 'mt-1 text-xs font-normal')} value={promise.id} onChange={(e) => setPromiseId(e.target.value)} aria-describedby="brief-promise-note">
            {promises.data!.map((p) => <option key={p.id} value={p.id}>{p.requirementId} · {p.title}</option>)}
          </select>
          <span id="brief-promise-note" className="mt-1 block font-normal leading-relaxed">
            {promise.state === 'approved'
              ? 'The brief holds the approved revision, what it links and its checks.'
              : promise.state === 'retired'
                ? 'This promise is retired. The brief lists it and holds no requirement for it.'
                : 'This promise is not approved. The brief lists it under “do not implement” and holds no requirement for it.'}
          </span>
        </label>
      )}
      {kind === 'change' && scan && (
        <label className="mt-2 block text-[11px] font-medium text-muted-foreground">
          Scan
          <select className={cn(selectClass, 'mt-1 text-xs font-normal')} value={scan.id} onChange={(e) => setScanId(e.target.value)} aria-describedby="brief-scan-note">
            {completed.map((s) => <option key={s.id} value={s.id}>{s.repository} · {s.ref ?? s.trackedRef ?? 'default branch'} · {s.commitSha?.slice(0, 10)} · {s.kind}</option>)}
          </select>
          <span id="brief-scan-note" className="mt-1 block font-normal leading-relaxed">
            The brief selects every promise, scenario and element that the impact of this scan names, and holds the impact report.
          </span>
        </label>
      )}
      {kind === 'elements' && (
        <fieldset className="mt-2">
          <legend className="text-[11px] font-medium text-muted-foreground">Elements <span className="font-mono">{elementIds.length}</span></legend>
          <ul className="mt-1 max-h-36 space-y-0.5 overflow-y-auto rounded-md border border-border p-1.5">
            {elements.map((element) => (
              <li key={element.id}>
                <label className="flex items-center gap-1.5 text-xs text-foreground">
                  <input
                    type="checkbox" className="h-3 w-3 accent-[var(--primary)]" checked={elementIds.includes(element.id)}
                    onChange={() => setElementIds(elementIds.includes(element.id) ? elementIds.filter((id) => id !== element.id) : [...elementIds, element.id])}
                  />
                  <span className="truncate">{element.name}</span>
                </label>
              </li>
            ))}
          </ul>
          <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">The elements that are selected on the canvas are ticked when this panel opens. An element brings its operations, subscriptions and messages.</p>
        </fieldset>
      )}
      {!chosen && <p className="mt-2 text-xs text-muted-foreground">{missing}</p>}

      <Button size="sm" variant="secondary" className="mt-2 w-full" disabled={!chosen} onClick={() => chosen && setPreview(chosen)}>
        Preview the brief
      </Button>
      {preview && <BriefDialog projectId={projectId} {...preview} onClose={() => setPreview(null)} />}
    </section>
  )
}
