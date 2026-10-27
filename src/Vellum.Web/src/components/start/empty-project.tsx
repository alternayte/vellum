import { useState, type FormEvent, type ReactNode } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { FolderOpen, GitBranch, Keyboard, LayoutTemplate, Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { FolderPickerButton, FolderUploadProgress } from '@/components/import/folder-picker'
import { useFolderUpload } from '@/hooks/use-folder-upload'
import { sourcesKey } from '@/hooks/use-sources'
import { errorText, sourcesApi } from '@/lib/import-api'
import { shownInline } from '@/lib/mutation-errors'
import { openTemplate, TEMPLATES } from '@/lib/templates'

interface Props {
  projectId: string
  /** Opens the Type it box over the canvas. */
  onTypeIt: () => void
  /** Sends the prose to the AI and shows what it proposes. Rejects with the reason when the AI cannot answer. */
  onDescribe: (prose: string) => Promise<void>
  /** A scan of a new folder or repository was queued: the page shows the Sources. */
  onSourceStarted: () => void
  /** A template is in the model: the page reloads it and lays it out. */
  onTemplateOpened: () => Promise<void>
  /** Opens the Add Element dialog for a first system, as the blank canvas did. */
  onAddSystem: () => void
}

/**
 * The screen of a project whose model holds no element. It offers three starts: point at
 * code, describe or type the system, or start from a template.
 */
export function EmptyProject({ projectId, onTypeIt, onDescribe, onSourceStarted, onTemplateOpened, onAddSystem }: Props) {
  return (
    // m-auto, not items-center: a screen shorter than the starts scrolls from their top.
    <div className="flex h-full overflow-y-auto p-4 sm:p-6">
      <div className="m-auto w-full max-w-4xl">
        <h1 className="font-display text-2xl font-bold">Start your diagram</h1>
        <p className="mt-1 text-sm text-muted-foreground">Pick one way in. You can use the others later.</p>
        <div className="mt-6 grid gap-4 md:grid-cols-3">
          <FromCode projectId={projectId} onStarted={onSourceStarted} />
          <FromWords onTypeIt={onTypeIt} onDescribe={onDescribe} />
          <FromTemplate projectId={projectId} onOpened={onTemplateOpened} />
        </div>
        <p className="mt-4 text-xs text-muted-foreground">
          Or draw it yourself:{' '}
          <button type="button" className="text-primary underline-offset-2 hover:underline" onClick={onAddSystem}>Add a system</button>.
        </p>
      </div>
    </div>
  )
}

function Start({ icon, title, text, children }: { icon: ReactNode; title: string; text: string; children: ReactNode }) {
  return (
    <section className="flex flex-col rounded-lg border border-border bg-card p-4" aria-label={title}>
      <div className="flex items-center gap-2 text-foreground">
        {icon}
        <h2 className="text-sm font-semibold">{title}</h2>
      </div>
      <p className="mt-1 text-xs text-muted-foreground">{text}</p>
      <div className="mt-4 flex flex-1 flex-col gap-2">{children}</div>
    </section>
  )
}

function FromCode({ projectId, onStarted }: { projectId: string; onStarted: () => void }) {
  const queryClient = useQueryClient()
  const folder = useFolderUpload(projectId)
  const [url, setUrl] = useState('')
  const [done, setDone] = useState<string | null>(null)

  const github = useMutation({
    mutationFn: async (repository: string) => {
      // A second click on the same repository scans its Source again.
      const key = repository.toLowerCase().replace(/^(https?:\/\/)?(www\.)?github\.com\//, '').replace(/(\.git)?\/*$/, '')
      const existing = (await sourcesApi.list(projectId)).find((s) => s.kind === 'github' && s.locator.toLowerCase() === key)
      const source = existing ?? await sourcesApi.create(projectId, { kind: 'github', repository })
      await sourcesApi.scan(projectId, source.id, {})
      return source.name
    },
    onSuccess: async (name) => {
      setDone(`Vellum reads ${name} now.`)
      await queryClient.invalidateQueries({ queryKey: sourcesKey(projectId) })
      onStarted()
    },
    onError: shownInline,
  })

  const pick = (picked: File[]) => {
    setDone(null)
    github.reset()
    folder.run.mutate({ picked }, {
      onSuccess: (result) => {
        setDone(`Uploaded ${result.files} ${result.files === 1 ? 'file' : 'files'} from ${result.sourceName}${result.ignored > 0 ? ` (${result.ignored} left out by .gitignore)` : ''}. Vellum reads them now.`)
        onStarted()
      },
    })
  }

  const submitUrl = (e: FormEvent) => {
    e.preventDefault()
    if (!url.trim() || github.isPending) return
    setDone(null)
    folder.run.reset()
    github.mutate(url.trim())
  }

  const busy = folder.run.isPending || github.isPending
  return (
    <Start
      icon={<FolderOpen className="h-4 w-4" aria-hidden="true" />}
      title="Choose folder / GitHub URL"
      text="Vellum reads the code and draws what it finds. The browser reads a copy of the folder and never changes it; .gitignore applies."
    >
      <FolderPickerButton
        onPick={pick}
        disabled={busy}
        className="inline-flex h-8 items-center justify-center gap-1.5 rounded-lg bg-primary px-3 text-sm font-medium text-primary-foreground hover:bg-primary/90"
      >
        <FolderOpen className="h-4 w-4" aria-hidden="true" /> Choose folder
      </FolderPickerButton>
      <form onSubmit={submitUrl} className="flex gap-1.5">
        <Input
          aria-label="GitHub repository"
          placeholder="github.com/owner/repo"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          spellCheck={false}
          disabled={busy}
        />
        <Button type="submit" variant="outline" disabled={busy || !url.trim()} aria-label="Read the repository">
          <GitBranch className="h-4 w-4" aria-hidden="true" /> Read
        </Button>
      </form>
      <FolderUploadProgress packing={folder.packing} sent={folder.sent} uploading={folder.uploading} />
      {github.isPending && <p role="status" className="text-[11px] text-muted-foreground">Adding the repository…</p>}
      {folder.run.isError && <p role="alert" className="text-xs text-destructive">{errorText(folder.run.error)}</p>}
      {github.isError && <p role="alert" className="text-xs text-destructive">{errorText(github.error)}</p>}
      {done && <p role="status" className="text-xs text-foreground">{done}</p>}
    </Start>
  )
}

function FromWords({ onTypeIt, onDescribe }: { onTypeIt: () => void; onDescribe: (prose: string) => Promise<void> }) {
  const [prose, setProse] = useState('')
  const describe = useMutation({ mutationFn: onDescribe, onError: shownInline })
  return (
    <Start
      icon={<Sparkles className="h-4 w-4" aria-hidden="true" />}
      title="Describe it / Type it"
      text="Describe the system in your words and the AI proposes a diagram for you to accept. Or type it one line per fact, with no AI."
    >
      <form
        className="flex flex-1 flex-col gap-2"
        onSubmit={(e) => { e.preventDefault(); if (prose.trim() && !describe.isPending) describe.mutate(prose.trim()) }}
      >
        <Textarea
          aria-label="Describe your system"
          placeholder="A shop: customers order on a web app, the Orders API stores orders in Postgres and calls a payment provider."
          className="min-h-20 flex-1 text-sm"
          value={prose}
          onChange={(e) => setProse(e.target.value)}
        />
        <Button type="submit" variant="outline" disabled={!prose.trim() || describe.isPending}>
          <Sparkles className="h-4 w-4" aria-hidden="true" /> {describe.isPending ? 'The AI is working…' : 'Describe it'}
        </Button>
      </form>
      {describe.isError && <p role="alert" className="text-xs text-destructive">{describe.error.message} Type it works without AI.</p>}
      <Button type="button" variant="outline" onClick={onTypeIt}>
        <Keyboard className="h-4 w-4" aria-hidden="true" /> Type it
      </Button>
    </Start>
  )
}

function FromTemplate({ projectId, onOpened }: { projectId: string; onOpened: () => Promise<void> }) {
  const open = useMutation({
    mutationFn: async (templateId: string) => {
      await openTemplate(projectId, templateId)
      await onOpened()
    },
    onError: shownInline,
  })
  return (
    <Start
      icon={<LayoutTemplate className="h-4 w-4" aria-hidden="true" />}
      title="Start from a template"
      text="A small design with contracts, scenarios to play and a Stress profile to run. It is a copy: change it as you like."
    >
      <ul className="flex flex-col gap-1.5" aria-label="Templates">
        {TEMPLATES.map((template) => (
          <li key={template.id}>
            <button
              type="button"
              disabled={open.isPending}
              onClick={() => open.mutate(template.id)}
              className="w-full rounded-md border border-border px-2.5 py-1.5 text-left transition-colors hover:border-primary/60 hover:bg-muted disabled:opacity-60"
            >
              <span className="flex items-baseline justify-between gap-2">
                <span className="text-sm font-medium">{template.name}</span>
                <span className="shrink-0 text-[11px] tabular-nums text-muted-foreground">
                  {open.isPending && open.variables === template.id ? 'Opening…' : `${template.elements} elements`}
                </span>
              </span>
              <span className="mt-0.5 block text-[11px] leading-snug text-muted-foreground">{template.description}</span>
            </button>
          </li>
        ))}
      </ul>
      {open.isError && <p role="alert" className="text-xs text-destructive">{errorText(open.error)}</p>}
    </Start>
  )
}
