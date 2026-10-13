import { useState } from 'react'
import { Check, Copy, Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import type { K6Script } from '@/lib/load-tests-api'

interface Props {
  script: K6Script
  onClose: () => void
}

/** A button that copies text and says so for a moment, also to a screen reader. */
function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false)
  const [failed, setFailed] = useState(false)
  return (
    <Button
      type="button" variant="outline" size="sm" className="h-7 gap-1.5 px-2 text-xs"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text)
          setCopied(true)
          setFailed(false)
          window.setTimeout(() => setCopied(false), 2000)
        } catch {
          // The browser refused the clipboard. The text stays on screen to select by hand.
          setFailed(true)
        }
      }}
    >
      {copied ? <Check aria-hidden className="h-3 w-3" /> : <Copy aria-hidden className="h-3 w-3" />}
      <span aria-live="polite">{copied ? 'Copied' : failed ? 'Copy failed: select the text' : label}</span>
    </Button>
  )
}

function download(fileName: string, content: string) {
  const url = URL.createObjectURL(new Blob([content], { type: 'text/javascript' }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  URL.revokeObjectURL(url)
}

const pre = 'overflow-auto rounded-md border border-border bg-background px-3 py-2 font-mono text-[11px] leading-relaxed text-foreground'

/**
 * A generated k6 script: what it drives, what it leaves out, the command that runs it, a
 * CI workflow and the text of the script. The same inputs give the same text and hash.
 */
export function K6ScriptDialog({ script, onClose }: Props) {
  const content = script.content ?? ''
  const { plan } = script
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[94vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>k6 script for {script.targetName}</DialogTitle>
          <DialogDescription>
            You run this script with k6 on your machine or in CI. Vellum does not run it and sends no request to the target.
            <span className="mt-1 block font-mono text-[11px]">
              {script.fileName} · {script.hash.slice(0, 12)} · scenario version {script.scenarioVersion} · model version {script.modelVersion} · profile v{script.profileVersion} · {script.environment}
            </span>
          </DialogDescription>
        </DialogHeader>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
          <section aria-labelledby="k6-covers" className="grid gap-3 md:grid-cols-2">
            <div className="rounded-md border border-border px-3 py-2">
              <h3 id="k6-covers" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Driven steps</h3>
              <ol className="mt-1.5 list-decimal space-y-1 pl-4 text-xs text-foreground">
                {plan.steps.map((step) => (
                  <li key={step.stepId}>
                    <span className="font-mono">{step.method} {step.path}</span>
                    <span className="block text-[11px] text-muted-foreground">
                      step {step.stepId} · {plan.operations[step.operation]?.name} · expects {step.expect === 'success' ? 'a 2xx status' : 'no 2xx status'}
                    </span>
                  </li>
                ))}
              </ol>
            </div>
            <div className="rounded-md border border-[var(--trace-retry)]/60 px-3 py-2">
              <h3 className="text-xs font-medium uppercase tracking-wider text-[var(--trace-retry)]">Not covered</h3>
              {plan.notCovered.length === 0 ? (
                <p className="mt-1.5 text-xs text-foreground">Nothing. The script drives every step of the scenario.</p>
              ) : (
                <ul className="mt-1.5 space-y-1 text-xs text-foreground">
                  {plan.notCovered.map((skipped) => (
                    <li key={skipped.stepId}>
                      <span className="font-mono">{skipped.stepId}</span> <span className="text-[11px] text-muted-foreground">({skipped.kind})</span>
                      <span className="block text-[11px] text-muted-foreground">{skipped.reason}</span>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </section>

          {(plan.notes.length > 0 || plan.faults > 0) && (
            <ul className="list-disc space-y-0.5 pl-4 text-[11px] leading-relaxed text-muted-foreground">
              {plan.faults > 0 && <li className="text-[var(--trace-retry)]">The scenario declares {plan.faults} fault(s). The script does not inject them: inject them in the target system before the run.</li>}
              {plan.notes.map((note, i) => <li key={i}>{note}</li>)}
            </ul>
          )}

          <section aria-labelledby="k6-run" className="space-y-1.5">
            <div className="flex items-center justify-between gap-2">
              <h3 id="k6-run" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Run it</h3>
              <CopyButton text={script.command} label="Copy the command" />
            </div>
            <pre className={pre}>{script.command}</pre>
            <p className="text-[11px] leading-relaxed text-muted-foreground">
              Set <span className="font-mono">VELLUM_AUTHORIZATION</span> in the environment of k6 when the target needs a credential. The script holds none.
              The run writes <span className="font-mono">vellum-k6-result.json</span>. Import that file here to get measured values.
            </p>
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer rounded text-foreground focus-visible:outline-2 focus-visible:outline-primary">GitHub Actions workflow</summary>
              <div className="mt-1.5 space-y-1.5">
                <div className="flex justify-end"><CopyButton text={script.ciWorkflow} label="Copy the workflow" /></div>
                <pre className={`${pre} max-h-56`}>{script.ciWorkflow}</pre>
                <p className="text-[11px] leading-relaxed">Commit the script under <span className="font-mono">load-tests/</span>. The workflow keeps the result file as an artifact.</p>
              </div>
            </details>
          </section>

          <section aria-labelledby="k6-text" className="space-y-1.5">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 id="k6-text" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Script</h3>
              <div className="flex gap-2">
                <CopyButton text={content} label="Copy the script" />
                <Button type="button" size="sm" className="h-7 gap-1.5 px-2 text-xs" onClick={() => download(script.fileName, content)}>
                  <Download aria-hidden className="h-3 w-3" /> Download {script.fileName}
                </Button>
              </div>
            </div>
            {/* The script is text from the model inside string literals. It is shown as text, never run here. */}
            <pre className={`${pre} max-h-[26rem]`} tabIndex={0} aria-label="Text of the k6 script">{content}</pre>
          </section>
        </div>
      </DialogContent>
    </Dialog>
  )
}
