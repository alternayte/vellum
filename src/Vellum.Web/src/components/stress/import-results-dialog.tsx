import { useId, useRef, useState, type DragEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { FileUp } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError } from '@/lib/settings-api'
import { lineOfMember, loadTestsApi, LoadTestRefused, type FieldProblem, type MeasuredResult } from '@/lib/load-tests-api'

interface Props {
  projectId: string
  onClose: () => void
  onImported: (result: MeasuredResult) => void
}

/** The file must fit the limit of the server. A larger file is refused here, before it is read. */
const MAX_BYTES = 256 * 1024

/**
 * Imports the result file of a k6 run: drop the file, choose it, or paste its text. A file
 * that is refused is not stored, and each error names the member and the line that causes it.
 */
export function ImportResultsDialog({ projectId, onClose, onImported }: Props) {
  const ids = useId()
  const fileInput = useRef<HTMLInputElement>(null)
  const textArea = useRef<HTMLTextAreaElement>(null)
  const [text, setText] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const [problem, setProblem] = useState<{ title: string; detail: string | null; errors: FieldProblem[] } | null>(null)

  const load = async (file: File | undefined) => {
    if (!file) return
    setProblem(null)
    if (file.size > MAX_BYTES) {
      setProblem({ title: 'The file is too large', detail: `A result file has at most 256 KB. This file has ${Math.ceil(file.size / 1024)} KB.`, errors: [] })
      return
    }
    setText(await file.text())
    setFileName(file.name)
  }

  const submit = useMutation({
    mutationFn: () => loadTestsApi.importResult(projectId, text),
    onMutate: () => setProblem(null),
    onSuccess: onImported,
    onError: (e) => {
      if (e instanceof LoadTestRefused) setProblem({ title: e.message, detail: e.detail, errors: e.errors })
      else if (e instanceof ApiError) setProblem({ title: e.fields.length > 0 ? 'The result file is not valid' : e.message, detail: null, errors: e.fields })
      else setProblem({ title: e.message, detail: null, errors: [] })
      shownInline()
    },
  })

  const onDrop = (e: DragEvent) => {
    e.preventDefault()
    setDragging(false)
    void load(e.dataTransfer.files[0])
  }

  /** Puts the cursor on the line of the member, so the cause is in view in the text. */
  const show = (line: number) => {
    const area = textArea.current
    if (!area) return
    const start = text.split('\n').slice(0, line - 1).join('\n').length + (line > 1 ? 1 : 0)
    const end = text.indexOf('\n', start)
    area.focus()
    area.setSelectionRange(start, end < 0 ? text.length : end)
    area.scrollTop = Math.max(0, (line - 4) * 16)
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[94vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Import k6 results</DialogTitle>
          <DialogDescription>
            Give the file that a run of a Vellum script wrote: <span className="font-mono">vellum-k6-result.json</span>, or the JSON of <span className="font-mono">k6 run --summary-export</span>.
            Vellum stores a file only when it names a script that Vellum generated for this project.
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
          <div
            onDragOver={(e) => { e.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            className={`flex flex-col items-center gap-2 rounded-md border border-dashed px-4 py-5 text-center text-xs ${dragging ? 'border-[var(--trace-request)] bg-[var(--trace-request)]/10 text-foreground' : 'border-border text-muted-foreground'}`}
          >
            <FileUp aria-hidden className="h-5 w-5" />
            <p>Drop the result file here</p>
            <input ref={fileInput} type="file" accept=".json,application/json" className="sr-only" id={`${ids}-file`} onChange={(e) => void load(e.target.files?.[0])} />
            <Button type="button" variant="outline" size="sm" className="h-7 px-2 text-xs" onClick={() => fileInput.current?.click()}>Choose a file</Button>
            {fileName && <p className="font-mono text-[11px] text-foreground">{fileName}</p>}
          </div>

          <label className="block text-[11px] font-medium text-muted-foreground" htmlFor={`${ids}-text`}>
            Or paste the text of the file
            <textarea
              id={`${ids}-text`} ref={textArea} value={text} spellCheck={false} rows={10}
              aria-invalid={problem !== null} aria-describedby={problem ? `${ids}-problem` : undefined}
              onChange={(e) => { setText(e.target.value); setFileName(null); setProblem(null) }}
              className="mt-1 w-full rounded-lg border border-input bg-transparent px-2.5 py-2 font-mono text-[11px] leading-4 text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-[var(--trace-failure)] dark:bg-input/30"
            />
          </label>

          {problem && (
            <div id={`${ids}-problem`} role="alert" className="rounded-md border border-[var(--trace-failure)]/60 px-3 py-2 text-xs">
              <p className="font-medium text-[var(--trace-failure)]">{problem.title}. Nothing was stored.</p>
              {problem.detail && <p className="mt-1 leading-relaxed text-foreground">{problem.detail}</p>}
              {problem.errors.length > 0 && (
                <ul className="mt-1.5 space-y-1.5">
                  {problem.errors.map((error, i) => {
                    const line = lineOfMember(text, error.field)
                    return (
                      <li key={i} className="text-foreground">
                        {line === null ? (
                          <span className="font-mono text-[11px] text-[var(--trace-failure)]">{error.field}</span>
                        ) : (
                          <button
                            type="button" onClick={() => show(line)}
                            className="rounded font-mono text-[11px] text-[var(--trace-failure)] underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-primary"
                          >
                            {error.field} · line {line}
                          </button>
                        )}
                        <span className="block leading-relaxed">{error.message}</span>
                      </li>
                    )
                  })}
                </ul>
              )}
            </div>
          )}
        </div>

        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button type="button" size="sm" disabled={submit.isPending || text.trim() === ''} onClick={() => submit.mutate()}>
            {submit.isPending ? 'Importing…' : 'Import'}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
