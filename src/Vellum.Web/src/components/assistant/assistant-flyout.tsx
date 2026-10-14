import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Square } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import {
  designApi, explain, isUnavailable,
  type ArchitectureProposal, type ExplainTarget, type FlowProposal, type PendingProposal, type Unavailable,
} from '@/lib/design-api'
import { useShellStore } from '@/stores/shell-store'
import { currentTraceEvent, useTraceStore } from '@/stores/trace-store'
import { ArchitectureProposalDialog } from './architecture-proposal-dialog'
import { FlowProposalDialog } from './flow-proposal-dialog'
import { UnavailableNote } from './unavailable-note'

type Mode = 'flow' | 'architecture' | 'explain'

interface AssistantFlyoutProps {
  projectId: string
  branchId?: string | null
  labels: Record<string, string>
  /** The scenario whose trace is open, when one is. */
  playingScenarioId: string | null
  onPlayScenario: (scenarioId: string) => void
}

const MODES: { id: Mode; label: string }[] = [
  { id: 'flow', label: 'Flow' },
  { id: 'architecture', label: 'Model' },
  { id: 'explain', label: 'Explain' },
]

const textareaClass =
  'min-h-28 w-full resize-y rounded-lg border border-input bg-transparent p-2.5 text-sm text-foreground outline-none placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50'

/**
 * The assistant proposes; it never changes the model or a scenario by itself. Every answer
 * opens as a proposal that you accept, in part or in whole, or reject.
 */
export function AssistantFlyout({ projectId, branchId, labels, playingScenarioId, onPlayScenario }: AssistantFlyoutProps) {
  const [mode, setMode] = useState<Mode>('flow')
  const [request, setRequest] = useState('')
  const [unavailable, setUnavailable] = useState<Unavailable | null>(null)
  const [flow, setFlow] = useState<FlowProposal | null>(null)
  const [architecture, setArchitecture] = useState<ArchitectureProposal | null>(null)
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const queryClient = useQueryClient()

  // What agents submitted and nobody decided yet. A person opens each one here and decides.
  const pending = useQuery({ queryKey: ['projects', projectId, 'proposals', 'pending'], queryFn: () => designApi.pending(projectId) })
  // Every list of proposals: what waits for you here, and "Your proposals" and "History" in Review.
  const refreshPending = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'proposals'] })
  const open = useMutation({
    mutationFn: (item: PendingProposal): Promise<FlowProposal | ArchitectureProposal> =>
      item.kind === 'flow' ? designApi.getFlow(projectId, item.id) : designApi.getArchitecture(projectId, item.id),
    onSuccess: (result) => { if ('scenario' in result) setFlow(result); else setArchitecture(result) },
    onError: shownInline,
  })

  const propose = useMutation({
    mutationFn: (): Promise<FlowProposal | ArchitectureProposal | Unavailable> =>
      mode === 'flow' ? designApi.proposeFlow(projectId, request.trim(), branchId) : designApi.proposeArchitecture(projectId, request.trim(), branchId),
    onSuccess: (result) => {
      if (isUnavailable(result)) setUnavailable(result)
      else if ('scenario' in result) setFlow(result)
      else setArchitecture(result)
    },
    onError: shownInline,
  })

  const submit = (e: FormEvent) => {
    e.preventDefault()
    setUnavailable(null)
    propose.mutate()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Assistant</span>
      </div>
      <div role="tablist" aria-label="Assistant task" className="flex gap-1 border-b border-border p-2">
        {MODES.map((m) => (
          <button
            key={m.id}
            type="button"
            role="tab"
            aria-selected={mode === m.id}
            onClick={() => { setMode(m.id); setUnavailable(null); propose.reset() }}
            className={cn('flex-1 rounded-md px-2 py-1 text-xs text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary', mode === m.id && 'bg-muted text-foreground')}
          >
            {m.label}
          </button>
        ))}
      </div>

      <div className="flex-1 space-y-3 overflow-y-auto p-3">
        {mode === 'explain' ? (
          <ExplainPanel projectId={projectId} branchId={branchId} labels={labels} playingScenarioId={playingScenarioId} />
        ) : (
          <form onSubmit={submit} className="space-y-2">
            <label className="block text-[11px] font-medium text-muted-foreground">
              {mode === 'flow' ? 'Describe the behaviour' : 'Describe what to add'}
              <textarea
                className={cn(textareaClass, 'mt-1')}
                value={request}
                onChange={(e) => setRequest(e.target.value)}
                maxLength={4000}
                placeholder={mode === 'flow'
                  ? 'A customer places an order. Orders saves it and publishes OrderCreated. Show what happens when Billing fails.'
                  : 'Add a notification service that listens to OrderCreated and sends an email.'}
                required
              />
            </label>
            <Button type="submit" size="sm" className="w-full" disabled={propose.isPending || !request.trim()}>
              {propose.isPending ? 'The assistant is working…' : mode === 'flow' ? 'Propose a flow' : 'Propose additions'}
            </Button>
            <p className="text-[11px] leading-relaxed text-muted-foreground">
              {mode === 'flow'
                ? 'The answer is a proposal that uses only parts of your model. You review it before anything is saved.'
                : 'The answer lists additions. You select which ones to add. Each is labelled inferred.'}
              {' '}A proposal you close without a decision stays under Review, Assistant, Your proposals.
            </p>
          </form>
        )}
        {unavailable && mode !== 'explain' && <UnavailableNote state={unavailable} />}
        {propose.isError && <p role="alert" className="text-xs text-destructive">{propose.error.message}</p>}

        {pending.data && pending.data.length > 0 && (
          <section aria-labelledby="pending-proposals" className="border-t border-border pt-3">
            <h3 id="pending-proposals" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
              Waiting for you <span className="font-mono">{pending.data.length}</span>
            </h3>
            <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
              An agent submitted these proposals. Nothing is in the model or saved as a scenario until you accept it.
            </p>
            <ul className="mt-1.5 space-y-1">
              {pending.data.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    disabled={open.isPending}
                    onClick={() => open.mutate(item)}
                    className="w-full rounded-md border border-border px-2.5 py-2 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                  >
                    <p className="flex items-baseline gap-2">
                      <span className="shrink-0 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{item.kind === 'flow' ? 'flow' : 'model'}</span>
                      <span className="min-w-0 flex-1 truncate text-sm text-foreground">{item.name}</span>
                    </p>
                    {/* The request is text that the agent wrote. It is shown as text. */}
                    <p className="mt-0.5 line-clamp-2 text-[11px] text-muted-foreground">{item.request}</p>
                    <p className="mt-0.5 font-mono text-[10px] text-[var(--trace-retry)]">
                      {item.origin.startsWith('agent-token:') ? `agent token ${item.origin.slice(12, 20)}` : 'over the API'} · {new Date(item.createdAt).toLocaleString()}
                    </p>
                  </button>
                </li>
              ))}
            </ul>
            {open.isError && <p role="alert" className="mt-1 text-xs text-destructive">{open.error.message}</p>}
          </section>
        )}
      </div>

      {flow && (
        <FlowProposalDialog
          projectId={projectId}
          proposal={flow}
          labels={labels}
          onClose={() => { setFlow(null); void refreshPending() }}
          onAccepted={(scenarioId) => { setFlow(null); void refreshPending(); closeFlyout(); onPlayScenario(scenarioId) }}
        />
      )}
      {architecture && (
        <ArchitectureProposalDialog projectId={projectId} proposal={architecture} labels={labels} onClose={() => { setArchitecture(null); void refreshPending() }} />
      )}
    </div>
  )
}

function ExplainPanel({ projectId, branchId, labels, playingScenarioId }: Pick<AssistantFlyoutProps, 'projectId' | 'branchId' | 'labels' | 'playingScenarioId'>) {
  const selectedElementId = useShellStore((s) => s.selectedElementId)
  const traceEvent = useTraceStore(currentTraceEvent)
  const [question, setQuestion] = useState('')
  const [text, setText] = useState('')
  const [state, setState] = useState<'idle' | 'streaming' | 'done'>('idle')
  const [unavailable, setUnavailable] = useState<Unavailable | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [omitted, setOmitted] = useState(0)
  const abort = useRef<AbortController | null>(null)
  useEffect(() => () => abort.current?.abort(), [])

  // The trace event in view wins, because it is the more specific thing on screen.
  const target: ExplainTarget | null = traceEvent && playingScenarioId
    ? { kind: 'trace-event', scenarioId: playingScenarioId, sequence: traceEvent.sequence }
    : selectedElementId ? { kind: 'element', id: selectedElementId } : null
  const targetName = target?.kind === 'trace-event' ? `event ${target.sequence} of the trace` : target ? (labels[target.id] ?? 'the selected element') : null

  const start = async (e: FormEvent) => {
    e.preventDefault()
    if (!target) return
    abort.current?.abort()
    const controller = new AbortController()
    abort.current = controller
    setText(''); setError(null); setUnavailable(null); setOmitted(0); setState('streaming')
    try {
      const result = await explain(projectId, target, question.trim() || null, branchId, (delta) => setText((current) => current + delta), controller.signal, setOmitted)
      if (result) setUnavailable(result)
    } catch (err) {
      if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'The explanation stopped.')
    } finally {
      if (abort.current === controller) setState('done')
    }
  }

  return (
    <form onSubmit={start} className="space-y-2">
      <p className="text-xs text-foreground">
        {targetName ? <>Explain <span className="font-medium">{targetName}</span>.</> : 'Select an element on the canvas, or open a trace and go to an event.'}
      </p>
      <label className="block text-[11px] font-medium text-muted-foreground">
        Your question (optional)
        <textarea className={cn(textareaClass, 'mt-1 min-h-16')} value={question} onChange={(e) => setQuestion(e.target.value)} maxLength={2000} placeholder="Why does this step retry?" />
      </label>
      <div className="flex gap-2">
        <Button type="submit" size="sm" className="flex-1" disabled={!target || state === 'streaming'}>{state === 'streaming' ? 'Explaining…' : 'Explain'}</Button>
        {state === 'streaming' && (
          <Button type="button" size="sm" variant="ghost" className="gap-1" onClick={() => { abort.current?.abort(); setState('done') }}>
            <Square className="h-3 w-3" /> Stop
          </Button>
        )}
      </div>
      {unavailable && <UnavailableNote state={unavailable} />}
      {error && <p role="alert" className="text-xs text-destructive">{error}</p>}
      {omitted > 0 && (
        <p role="status" className="text-xs text-muted-foreground">
          The assistant did not see {omitted} {omitted === 1 ? 'part' : 'parts'} of the model that came from a private repository.
        </p>
      )}
      {text && (
        <div aria-live="polite" aria-busy={state === 'streaming'} className="rounded-md border border-border bg-background px-3 py-2">
          {/* The answer is model text. It is shown as text and never rendered as markup. */}
          <p className="whitespace-pre-wrap text-sm leading-relaxed text-foreground">{text}</p>
          <p className="mt-2 text-[11px] text-muted-foreground">An explanation from the assistant. It reads the declared model. It verifies nothing.</p>
        </div>
      )}
    </form>
  )
}
