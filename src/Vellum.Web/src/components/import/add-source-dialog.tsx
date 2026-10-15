import { useId, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Activity, Radar } from 'lucide-react'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { sourcesApi, type NewRelicRegion, type SourceSummary, type TraceUpload } from '@/lib/import-api'
import { sourcesKey } from '@/hooks/use-sources'

type Kind = 'traces' | 'newrelic'

const KINDS: { value: Kind; label: string; icon: typeof Activity }[] = [
  { value: 'traces', label: 'Trace file', icon: Activity },
  { value: 'newrelic', label: 'New Relic', icon: Radar },
]

const FILE_INPUT = 'block w-full rounded-lg border border-input text-sm text-foreground file:mr-3 file:border-0 file:bg-secondary file:px-3 file:py-1.5 file:text-sm file:text-secondary-foreground focus-visible:outline-2 focus-visible:outline-primary'

/**
 * Adds a runtime Source to the project. A trace file Source reads OTLP JSON, Jaeger or Tempo
 * exports that a person uploads; a New Relic Source reads one account's entities and their
 * relationships. Both only propose: nothing reaches the model until a person accepts it.
 */
export function AddSourceDialog({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const [kind, setKind] = useState<Kind>('traces')
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Add a runtime Source</DialogTitle>
          <DialogDescription>
            Runtime edges are labelled measured. Vellum proposes them; you accept each one.
          </DialogDescription>
        </DialogHeader>
        <div role="radiogroup" aria-label="Kind of Source" className="grid grid-cols-2 rounded-md border border-border p-0.5">
          {KINDS.map(({ value, label, icon: Icon }) => (
            <button
              key={value} type="button" role="radio" aria-checked={kind === value} onClick={() => setKind(value)}
              className={cn(
                'flex items-center justify-center gap-1.5 rounded px-2 py-1.5 text-xs text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                kind === value && 'bg-muted text-foreground',
              )}
            >
              <Icon className="h-3.5 w-3.5" aria-hidden="true" /> {label}
            </button>
          ))}
        </div>
        {kind === 'traces' ? <TraceForm projectId={projectId} onClose={onClose} /> : <NewRelicForm projectId={projectId} onClose={onClose} />}
      </DialogContent>
    </Dialog>
  )
}

function useRefresh(projectId: string) {
  const queryClient = useQueryClient()
  return () => Promise.all([
    queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) }),
    queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'imports'] }),
  ])
}

function TraceForm({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const id = useId()
  const refresh = useRefresh(projectId)
  const [name, setName] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const add = useMutation({
    mutationFn: async () => {
      const source = await sourcesApi.create(projectId, { kind: 'traces', name: name.trim() })
      return sourcesApi.uploadTraces(projectId, source.id, file!)
    },
    onSuccess: refresh,
    onError: shownInline,
  })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    add.mutate()
  }
  if (add.data) return <UploadDone upload={add.data} onClose={onClose} />
  return (
    <form onSubmit={submit} className="space-y-3">
      <label className="block text-xs font-medium text-muted-foreground">
        Name
        <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="For example: checkout traces" required maxLength={200} />
      </label>
      <div>
        <label htmlFor={`${id}-file`} className="mb-1 block text-xs font-medium text-muted-foreground">Trace export (OTLP JSON, Jaeger JSON or Tempo JSON)</label>
        <input id={`${id}-file`} type="file" accept=".json,application/json" required onChange={(e) => setFile(e.target.files?.[0] ?? null)} className={FILE_INPUT} />
      </div>
      <p className="text-xs leading-relaxed text-muted-foreground">
        Vellum reads the file once and keeps only service names, operation names, span kinds, peers and status. It keeps no span, no attribute value and no timing beyond the window.
      </p>
      {add.isError && <p role="alert" className="text-xs text-destructive">{add.error.message}</p>}
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={add.isPending || !file || !name.trim()}>{add.isPending ? 'Reading…' : 'Add and read file'}</Button>
      </DialogFooter>
    </form>
  )
}

/** What an upload derived. The proposals follow when the scan completes. */
export function UploadDone({ upload, onClose }: { upload: TraceUpload; onClose: () => void }) {
  return (
    <div className="space-y-3">
      <p role="status" className="text-sm text-foreground">
        Read {upload.spans} spans ({upload.format}): {upload.services} services and {upload.edges} edges. The proposals appear in the Source when the scan completes.
      </p>
      <p className="break-all font-mono text-[11px] text-muted-foreground">Snapshot {upload.snapshotId}</p>
      <DialogFooter><Button type="button" onClick={onClose}>Close</Button></DialogFooter>
    </div>
  )
}

function NewRelicForm({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const refresh = useRefresh(projectId)
  const [name, setName] = useState('')
  const [accountId, setAccountId] = useState('')
  const [region, setRegion] = useState<NewRelicRegion>('us')
  const [apiKey, setApiKey] = useState('')
  const account = Number(accountId)
  const valid = Number.isInteger(account) && account > 0 && apiKey.length > 0
  const test = useMutation({ mutationFn: () => sourcesApi.testNewRelic(projectId, { accountId: account, region, apiKey }), onError: shownInline })
  const add = useMutation({
    mutationFn: async (): Promise<SourceSummary> => {
      const source = await sourcesApi.create(projectId, { kind: 'newrelic', name: name.trim() || undefined, accountId: account, region, apiKey })
      await sourcesApi.scan(projectId, source.id, {})
      return source
    },
    onSuccess: async () => {
      setApiKey('')
      await refresh()
      onClose()
    },
    onError: shownInline,
  })
  const submit = (e: FormEvent) => {
    e.preventDefault()
    add.mutate()
  }
  const changed = () => test.reset()
  return (
    <form onSubmit={submit} className="space-y-3">
      <label className="block text-xs font-medium text-muted-foreground">
        Name
        <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="New Relic account …" maxLength={200} />
      </label>
      <div className="grid grid-cols-[1fr_auto] gap-2">
        <label className="block text-xs font-medium text-muted-foreground">
          Account ID
          <Input className="mt-1" value={accountId} onChange={(e) => { setAccountId(e.target.value.trim()); changed() }} inputMode="numeric" pattern="[0-9]+" required />
        </label>
        <fieldset>
          <legend className="text-xs font-medium text-muted-foreground">Region</legend>
          <div role="radiogroup" aria-label="Region" className="mt-1 grid grid-cols-2 rounded-md border border-border p-0.5">
            {(['us', 'eu'] as const).map((value) => (
              <button
                key={value} type="button" role="radio" aria-checked={region === value} onClick={() => { setRegion(value); changed() }}
                className={cn('rounded px-3 py-1 text-xs uppercase text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary', region === value && 'bg-muted text-foreground')}
              >
                {value}
              </button>
            ))}
          </div>
        </fieldset>
      </div>
      <label className="block text-xs font-medium text-muted-foreground">
        User API key (read-only)
        <Input className="mt-1" type="password" autoComplete="off" value={apiKey} onChange={(e) => { setApiKey(e.target.value); changed() }} required spellCheck={false} />
      </label>
      <p className="text-xs leading-relaxed text-muted-foreground">
        Vellum sends the key only to {region === 'eu' ? 'https://api.eu.newrelic.com' : 'https://api.newrelic.com'}. It stores the key encrypted and never shows it again. It reads entities and their relationships: no metrics, traces, logs or attributes.
      </p>
      {test.data && (
        <p role="status" className={cn('text-xs', test.data.ok ? 'text-[var(--trace-request)]' : 'text-destructive')}>
          {test.data.ok ? `Connected to “${test.data.accountName ?? accountId}”.` : test.data.error?.message}
        </p>
      )}
      {(test.isError || add.isError) && <p role="alert" className="text-xs text-destructive">{(add.error ?? test.error)!.message}</p>}
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
        <Button type="button" variant="secondary" disabled={!valid || test.isPending} onClick={() => test.mutate()}>{test.isPending ? 'Testing…' : 'Test connection'}</Button>
        <Button type="submit" disabled={!valid || add.isPending}>{add.isPending ? 'Adding…' : 'Add and read account'}</Button>
      </DialogFooter>
    </form>
  )
}
