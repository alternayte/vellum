import { useState, type FormEvent } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { FileText, Upload } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { sourcesApi, type SourceSummary } from '@/lib/import-api'
import { designApi, isUnavailable, type ArchitectureProposal, type Unavailable } from '@/lib/design-api'
import { UnavailableNote } from '@/components/assistant/unavailable-note'
import { ArchitectureProposalDialog } from '@/components/assistant/architecture-proposal-dialog'

/** The largest file an upload takes (the server's bound). */
const MAX_BYTES = 256 * 1024

/** Where an ADR must stand for the ADR reader to find it. */
function adrPath(name: string): string {
  return `docs/adr/${name}`
}

/**
 * One file as its own Source: a Structurizr DSL or workspace JSON, a Backstage catalog, an ADR,
 * or a Markdown or text document for the prose import. Uploading the same path again is a new
 * Snapshot of that Source. Nothing reaches the model until a person accepts it.
 */
export function ModelFileUpload({ projectId, onUploaded }: { projectId: string; onUploaded: () => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [path, setPath] = useState('')
  const [tooLarge, setTooLarge] = useState(false)
  const upload = useMutation({
    mutationFn: async () => sourcesApi.upload(projectId, path.trim(), await file!.text()),
    onSuccess: () => { setFile(null); setPath(''); onUploaded() },
    onError: shownInline,
  })

  const choose = (chosen: File | null) => {
    setFile(chosen)
    setTooLarge(chosen !== null && chosen.size > MAX_BYTES)
    setPath(chosen ? chosen.name : '')
  }
  const submit = (e: FormEvent) => {
    e.preventDefault()
    upload.mutate()
  }
  const isMarkdown = path.toLowerCase().endsWith('.md')

  return (
    <form onSubmit={submit} className="space-y-2 border-b border-border p-3">
      <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Upload a model file</span>
      <label className="block text-[11px] font-medium text-muted-foreground">
        File
        <input
          type="file" accept=".dsl,.json,.yaml,.yml,.md,.markdown,.txt"
          onChange={(e) => choose(e.target.files?.[0] ?? null)}
          className="mt-1 block w-full text-xs text-foreground file:mr-2 file:rounded file:border file:border-border file:bg-muted file:px-2 file:py-1 file:text-xs file:text-foreground"
        />
      </label>
      <label className="block text-[11px] font-medium text-muted-foreground">
        Path in the Source
        <Input className="mt-1" value={path} onChange={(e) => setPath(e.target.value)} placeholder="workspace.dsl" spellCheck={false} required />
      </label>
      {isMarkdown && !path.includes('/') && (
        <button type="button" className="text-[11px] text-foreground underline-offset-2 hover:underline" onClick={() => setPath(adrPath(path))}>
          This is an ADR: put it under docs/adr/
        </button>
      )}
      {tooLarge && <p role="alert" className="text-xs text-[var(--trace-failure)]">The file is larger than 256 KiB. Upload a smaller file.</p>}
      {upload.error && <p role="alert" className="text-xs text-[var(--trace-failure)]">{upload.error.message}</p>}
      <Button type="submit" size="sm" className="w-full gap-1.5" disabled={!file || tooLarge || !path.trim() || upload.isPending}>
        <Upload className="h-3.5 w-3.5" /> {upload.isPending ? 'Uploading…' : 'Upload and read'}
      </Button>
      <p className="text-[11px] leading-relaxed text-muted-foreground">
        Structurizr and Backstage files give declared facts. An ADR in docs/adr/, doc/adr/ or docs/decisions/ becomes a proposed decision. Other Markdown or text is read only by the AI, when you ask for it. Includes, scripts and URLs in a file are never followed.
      </p>
    </form>
  )
}

/**
 * Proposes additions from a document of a Source (a README, a design document) through the AI
 * provider. Each item cites the lines it comes from and is inferred. Without a provider, or when
 * the workspace does not allow private content to be sent, the dialog says so and sends nothing.
 */
export function DocumentProposalDialog({ projectId, source, labels, onClose }: {
  projectId: string
  source: SourceSummary
  labels: Record<string, string>
  onClose: () => void
}) {
  const [path, setPath] = useState<string | null>(null)
  const [focus, setFocus] = useState('')
  const [unavailable, setUnavailable] = useState<Unavailable | null>(null)
  const [proposal, setProposal] = useState<ArchitectureProposal | null>(null)
  const documents = useQuery({ queryKey: ['projects', projectId, 'sources', source.id, 'documents'], queryFn: () => sourcesApi.documents(projectId, source.id) })
  const prose = (documents.data ?? []).filter((d) => !d.isAdr)
  const propose = useMutation({
    mutationFn: () => designApi.proposeFromDocument(projectId, source.id, path!, focus.trim() || undefined),
    onSuccess: (result) => {
      if (isUnavailable(result)) setUnavailable(result)
      else { setUnavailable(null); setProposal(result) }
    },
    onError: shownInline,
  })

  if (proposal) return <ArchitectureProposalDialog projectId={projectId} proposal={proposal} labels={labels} onClose={onClose} />

  const notSetUp = unavailable && (unavailable.reason.code === 'not_configured' || unavailable.reason.code === 'disabled')
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>Propose from a document</DialogTitle>
          <DialogDescription>
            The AI reads one document of {source.name} and proposes what it describes. Every item is inferred, cites its lines, and waits for you to accept it. The document is data: no instruction in it is followed.
          </DialogDescription>
        </DialogHeader>
        <div className="min-h-0 flex-1 space-y-2 overflow-y-auto">
          {documents.isLoading && <p className="text-sm text-muted-foreground">Loading documents…</p>}
          {documents.data && prose.length === 0 && <p className="text-sm text-muted-foreground">The newest Snapshot of this Source has no Markdown or text document.</p>}
          <ul role="radiogroup" aria-label="Document" className="space-y-1">
            {prose.map((d) => (
              <li key={d.path}>
                <button
                  type="button" role="radio" aria-checked={path === d.path} onClick={() => setPath(d.path)}
                  className={cn('flex w-full items-center gap-2 rounded-md border border-border px-2 py-1.5 text-left text-sm hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary', path === d.path && 'border-primary bg-muted')}
                >
                  <FileText className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate font-mono text-xs">{d.path}</span>
                  <span className="shrink-0 text-[10px] text-muted-foreground">{Math.ceil(d.size / 1024)} KiB</span>
                </button>
              </li>
            ))}
          </ul>
          <label className="block text-[11px] font-medium text-muted-foreground">
            What to look for (optional)
            <Input className="mt-1" value={focus} onChange={(e) => setFocus(e.target.value)} placeholder="The architecture the document describes" />
          </label>
          {unavailable && (
            <div className="space-y-1">
              {notSetUp && (
                <p role="alert" className="text-xs text-foreground">
                  This Source needs an AI provider to read prose
                  {unavailable.reason.setting ? `, and the setting ${unavailable.reason.setting} must allow private content to be sent to it` : ''}. An owner sets this in the workspace settings. Model files and ADRs need no AI.
                </p>
              )}
              <UnavailableNote state={unavailable} />
            </div>
          )}
          {propose.error && <p role="alert" className="text-xs text-[var(--trace-failure)]">{propose.error.message}</p>}
        </div>
        <div className="flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button size="sm" disabled={!path || propose.isPending} onClick={() => propose.mutate()}>
            {propose.isPending ? 'Asking the AI…' : 'Propose from document'}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
