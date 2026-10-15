import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { ARCHIVE_ACCEPT, KIND_INFO, formatBytes, isGitHost, sourcesApi, uploadCap, type AddableKind, type SourceSummary } from '@/lib/import-api'
import { sourcesKey } from '@/hooks/use-sources'
import { useArchiveUpload } from '@/hooks/use-archive-upload'
import { UploadProgress } from './archive-upload'

const labelClass = 'block text-xs font-medium text-muted-foreground'

/**
 * Adds one Source to the project: a repository on a git host, a folder under the sources root
 * of this instance, or an uploaded archive. The first scan starts at once for a repository and
 * a folder; an archive is scanned when its file is uploaded.
 */
export function AddSourceDialog({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const kinds = useQuery({ queryKey: ['projects', projectId, 'sources', 'kinds'], queryFn: () => sourcesApi.kinds(projectId) })
  const [chosen, setChosen] = useState<AddableKind | null>(null)
  const [name, setName] = useState('')
  const [repository, setRepository] = useState('')
  const [path, setPath] = useState('')
  const [ref, setRef] = useState('')
  const [file, setFile] = useState<File | null>(null)
  // An archive Source exists before its first upload. A refused upload keeps it, so the next file goes to the same Source.
  const [created, setCreated] = useState<SourceSummary | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const { upload, sent } = useArchiveUpload(projectId)

  const firstAvailable = kinds.data?.kinds.find((k) => k.available)?.kind ?? null
  const kind = chosen ?? firstAvailable
  const info = kinds.data?.kinds.find((k) => k.kind === kind)
  const refresh = () => queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) })

  const add = useMutation({
    mutationFn: async () => {
      if (!kind) throw new Error('Choose a kind of Source.')
      const source = created ?? await sourcesApi.create(projectId, {
        kind,
        name: name.trim() || undefined,
        repository: isGitHost(kind) ? repository.trim() : undefined,
        path: kind === 'directory' ? path.trim().replace(/^\/+/, '') : undefined,
      })
      setCreated(source)
      if (kind === 'archive') {
        await upload.mutateAsync({ sourceId: source.id, file: file! })
        return `${source.name} was added. The upload is checked and scanned now.`
      }
      try {
        await sourcesApi.scan(projectId, source.id, isGitHost(kind) && ref.trim() ? { ref: ref.trim() } : {})
      } catch (error) {
        throw new Error(`${source.name} was added, but its first scan did not start. ${(error as Error).message}`, { cause: error })
      }
      return `${source.name} was added. Its first scan is queued.`
    },
    onSuccess: (message) => { setDone(message); return refresh() },
    // The error shows in the dialog. A Source may exist even so: the list must show it.
    onError: () => refresh(),
  })
  const pending = add.isPending

  const ready = kind !== null && info?.available === true && (
    isGitHost(kind) ? repository.trim() !== ''
    : kind === 'directory' ? path.trim() !== ''
    : (created !== null || name.trim() !== '') && file !== null
  )
  const tooLarge = kind === 'archive' && file !== null && kinds.data !== undefined && file.size > uploadCap(kinds.data.maxArchiveBytes)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (ready && !pending) add.mutate()
  }
  const choose = (next: AddableKind) => {
    if (created) return
    setChosen(next)
    add.reset()
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !pending) onClose() }}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Add a Source</DialogTitle>
          <DialogDescription>
            Vellum reads the files of a Source and proposes what it finds. It does not run the code, and it changes the model only when you accept a proposal.
          </DialogDescription>
        </DialogHeader>

        {kinds.isLoading && <p className="text-sm text-muted-foreground">Loading the kinds of Source…</p>}
        {kinds.isError && <p role="alert" className="text-sm text-destructive">{kinds.error.message}</p>}

        {done ? (
          <>
            <p role="status" className="text-sm text-foreground">{done}</p>
            <DialogFooter>
              <Button type="button" onClick={onClose} data-autofocus>Done</Button>
            </DialogFooter>
          </>
        ) : kinds.data && (
          <form onSubmit={submit} className="space-y-3">
            <fieldset>
              <legend className={labelClass}>Kind</legend>
              <div role="radiogroup" aria-label="Kind of Source" className="mt-1 grid grid-cols-2 gap-1.5 sm:grid-cols-3">
                {kinds.data.kinds.map((k) => (
                  <button
                    key={k.kind}
                    type="button"
                    role="radio"
                    aria-checked={kind === k.kind}
                    disabled={!k.available || (created !== null && created.kind !== k.kind)}
                    title={k.reason ?? undefined}
                    onClick={() => choose(k.kind)}
                    className={cn(
                      'rounded-md border border-border px-2 py-1.5 text-left text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary disabled:cursor-not-allowed disabled:text-muted-foreground disabled:hover:bg-transparent',
                      kind === k.kind && 'border-primary bg-muted',
                    )}
                  >
                    {KIND_INFO[k.kind].label}
                    {!k.available && <span className="block text-[10px] text-muted-foreground">Not available</span>}
                  </button>
                ))}
              </div>
            </fieldset>
            {kinds.data.kinds.some((k) => !k.available) && (
              <ul className="space-y-0.5 text-[11px] leading-relaxed text-muted-foreground">
                {kinds.data.kinds.filter((k) => !k.available).map((k) => (
                  <li key={k.kind}><span className="text-foreground">{KIND_INFO[k.kind].label}:</span> {k.reason}</li>
                ))}
              </ul>
            )}

            {kind && isGitHost(kind) && (
              <>
                <label className={labelClass}>
                  Repository
                  <Input
                    className="mt-1" value={repository} onChange={(e) => setRepository(e.target.value)}
                    placeholder={`${KIND_INFO[kind].locator}, or the address of its page`} spellCheck={false} data-autofocus
                  />
                  {info?.where && <span className="mt-1 block font-normal">On {info.where}.</span>}
                </label>
                <label className={labelClass}>
                  Branch or tag
                  <Input className="mt-1" value={ref} onChange={(e) => setRef(e.target.value)} placeholder="The default branch" spellCheck={false} />
                </label>
              </>
            )}

            {kind === 'directory' && (
              <label className={labelClass}>
                Folder
                <div className="mt-1 flex min-w-0 items-center rounded-lg border border-input focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50">
                  <span className="max-w-[55%] shrink-0 truncate border-r border-input px-2 py-1 font-mono text-xs text-muted-foreground" title={info?.where ?? undefined}>
                    {info?.where ?? ''}/
                  </span>
                  <input
                    className="h-8 min-w-0 flex-1 bg-transparent px-2 font-mono text-sm text-foreground outline-none"
                    value={path} onChange={(e) => setPath(e.target.value)} placeholder={KIND_INFO.directory.placeholder} spellCheck={false} data-autofocus
                  />
                </div>
                <span className="mt-1 block font-normal">A path under the sources root of this instance. Each scan reads the folder as it is then.</span>
              </label>
            )}

            {kind === 'archive' && (
              <>
                {!created && (
                  <label className={labelClass}>
                    Name
                    <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="orders-service" spellCheck={false} data-autofocus />
                    <span className="mt-1 block font-normal">Citations of this Source carry the name.</span>
                  </label>
                )}
                <label className={labelClass}>
                  File
                  <Input className="mt-1 py-1 text-xs" type="file" accept={ARCHIVE_ACCEPT} onChange={(e) => { setFile(e.target.files?.[0] ?? null); add.reset() }} />
                  <span className="mt-1 block font-normal">
                    A .zip or .tar.gz. Its files may expand to at most {formatBytes(kinds.data.maxArchiveBytes)}. Links, absolute paths and paths with “..” are refused.
                  </span>
                </label>
                {tooLarge && <p role="alert" className="text-[11px] text-destructive">This file is larger than one upload may be ({formatBytes(uploadCap(kinds.data.maxArchiveBytes))}). Choose a smaller archive.</p>}
                <UploadProgress sent={sent} pending={upload.isPending} />
              </>
            )}

            {kind && kind !== 'archive' && (
              <label className={labelClass}>
                Name
                <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder={kind === 'directory' ? 'The folder path' : 'The repository name'} spellCheck={false} />
              </label>
            )}

            {add.isError && <p role="alert" className="text-xs text-destructive">{add.error.message}</p>}
            {created && kind === 'archive' && add.isError && (
              <p className="text-[11px] text-muted-foreground">The Source {created.name} exists. Choose another file to upload to it.</p>
            )}

            <DialogFooter>
              <Button type="button" variant="ghost" disabled={pending} onClick={onClose}>Cancel</Button>
              <Button type="submit" disabled={!ready || pending || tooLarge}>
                {pending ? (kind === 'archive' ? 'Uploading…' : 'Adding…') : kind === 'archive' ? (created ? 'Upload' : 'Add and upload') : 'Add and scan'}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  )
}
