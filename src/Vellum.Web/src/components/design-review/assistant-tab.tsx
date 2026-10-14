import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Sparkles, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { UnavailableNote } from '@/components/assistant/unavailable-note'
import { isUnavailable, type Unavailable } from '@/lib/design-api'
import { shownInline } from '@/lib/mutation-errors'
import { useShellStore } from '@/stores/shell-store'
import { cn } from '@/lib/utils'
import { RESULT_LABEL, critiqueSections, reviewApi, type Cited, type Critique } from '@/lib/review-api'
import { CitedPart } from './findings-tab'
import { areaClass } from './styles'
import { ProposalLists } from './proposal-lists'

interface Props {
  projectId: string
  branchId?: string | null
  /** Names of the parts of the model, by ID, for the proposals. */
  labels: Record<string, string>
  focusTarget: (cited: Cited) => string | null
  onFocus: (elementId: string) => void
}

interface PanelProps extends Pick<Props, 'focusTarget' | 'onFocus'> {
  critique: Critique
  onKeepQuestion: (index: number) => void
  onKeepDecision: (index: number) => void
  keeping: boolean
}

function About({ about, focusTarget, onFocus }: { about: Cited[] } & Pick<Props, 'focusTarget' | 'onFocus'>) {
  if (about.length === 0) return null
  return <p className="mt-1 flex flex-wrap gap-1">{about.map((cited) => <CitedPart key={cited.id} cited={cited} focusTarget={focusTarget} onFocus={onFocus} />)}</p>
}

/**
 * One answer of the assistant. Everything in it is a suggestion: the panel has its own
 * frame and label, and nothing here is in the list of findings. A risk is shown as a risk
 * only when a finding of a rule supports it; every other claim is shown as a suspicion
 * that the model does not show.
 */
export function CritiquePanel({ critique, onKeepQuestion, onKeepDecision, keeping, focusTarget, onFocus }: PanelProps) {
  const sections = critiqueSections(critique)
  const empty = sections.risks.length + critique.questions.length + sections.proposedDecisions.length === 0
  const kept = <span className="text-[11px] text-muted-foreground">Kept</span>

  return (
    <section aria-label="Suggestions of the assistant" className="space-y-3 rounded-md border border-dashed border-[var(--trace-async,var(--primary))] p-3">
      <div>
        <p className="flex items-center gap-1.5 text-xs font-medium uppercase tracking-wider text-foreground">
          <Sparkles className="h-3.5 w-3.5" /> Suggestions of the assistant
        </p>
        <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
          These are suggestions, not findings. The assistant verified nothing. A suggestion changes nothing until you keep it, and only an owner accepts a decision.
        </p>
        {critique.stale && <p role="status" className="mt-1 text-xs text-[var(--trace-retry)]">The model changed after this answer was made. Ask again before you keep a proposed decision.</p>}
        {empty && <p className="mt-1 text-xs text-muted-foreground">The answer holds no suggestion that passed the checks.</p>}
      </div>

      {sections.risks.length > 0 && (
        <div data-testid="critique-risks">
          <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Risks that a finding supports</h4>
          <ul className="mt-1 space-y-1.5">
            {sections.risks.map((risk, i) => (
              <li key={i} className="rounded-md border border-border px-2.5 py-1.5">
                {/* Two sentences, each with its author: the finding is the rule's, the claim is the assistant's. */}
                <p className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
                  Finding <span className="font-mono normal-case">{risk.ruleId}</span>
                  {risk.findingResult ? <> · {RESULT_LABEL[risk.findingResult]}</> : null}
                </p>
                <p className="mt-0.5 text-xs leading-relaxed text-foreground">
                  {risk.findingReason ?? 'This answer was stored without the sentence of the finding. Read the finding in the Findings tab.'}
                </p>
                <p className="mt-1.5 text-[11px] font-medium uppercase tracking-wider text-muted-foreground">The assistant's sentence</p>
                <p className="mt-0.5 border-l-2 border-dashed border-border pl-2 text-xs italic leading-relaxed text-muted-foreground">{risk.claim}</p>
                <p className="mt-1 text-[11px] text-muted-foreground">The rule found only what the finding says. No check supports the wording of the assistant.</p>
                <About about={risk.about} focusTarget={focusTarget} onFocus={onFocus} />
              </li>
            ))}
          </ul>
        </div>
      )}

      {sections.questions.length > 0 && (
        <div>
          <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Questions</h4>
          <ul className="mt-1 space-y-1.5">
            {sections.questions.map(({ question, index }) => (
              <li key={index} className="rounded-md border border-border px-2.5 py-1.5">
                <p className="text-xs leading-relaxed text-foreground">{question.text}</p>
                {question.why && <p className="mt-0.5 text-[11px] text-muted-foreground">{question.why}</p>}
                <About about={question.about} focusTarget={focusTarget} onFocus={onFocus} />
                <p className="mt-1">
                  {question.keptAs ? kept : <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" disabled={keeping} onClick={() => onKeepQuestion(index)}>Keep as open question</Button>}
                </p>
              </li>
            ))}
          </ul>
        </div>
      )}

      {sections.suspicions.length > 0 && (
        <div data-testid="critique-suspicions">
          <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Suspicions the model does not show</h4>
          <ul className="mt-1 space-y-1.5">
            {sections.suspicions.map(({ question, index }) => (
              <li key={index} className="rounded-md border border-border px-2.5 py-1.5">
                <p className="text-xs leading-relaxed text-foreground">{question.text}</p>
                {question.note && <p className="mt-0.5 text-[11px] text-[var(--trace-retry)]">{question.note}</p>}
                <About about={question.about} focusTarget={focusTarget} onFocus={onFocus} />
                <p className="mt-1">
                  {question.keptAs ? kept : <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" disabled={keeping} onClick={() => onKeepQuestion(index)}>Keep as open question</Button>}
                </p>
              </li>
            ))}
          </ul>
        </div>
      )}

      {sections.proposedDecisions.length > 0 && (
        <div>
          <h4 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Proposed decisions</h4>
          <ul className="mt-1 space-y-1.5">
            {sections.proposedDecisions.map((proposed, index) => (
              <li key={index} className="rounded-md border border-border px-2.5 py-1.5">
                <p className="text-xs font-medium text-foreground">{proposed.title}</p>
                {proposed.context && <p className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">{proposed.context}</p>}
                <p className="mt-0.5 text-xs leading-relaxed text-foreground">{proposed.decision}</p>
                {proposed.alternatives.length > 0 && (
                  <ul className="mt-0.5 list-disc pl-4 text-[11px] text-muted-foreground">
                    {proposed.alternatives.map((a, i) => <li key={i}>{a.title}{a.whyNot ? `: ${a.whyNot}` : ''}</li>)}
                  </ul>
                )}
                {proposed.settles.length > 0 && (
                  <p className="mt-0.5 text-[11px] text-muted-foreground">Would answer <span className="font-mono">{[...new Set(proposed.settles.map((s) => s.ruleId))].join(', ')}</span> when an owner accepts it.</p>
                )}
                <About about={proposed.about} focusTarget={focusTarget} onFocus={onFocus} />
                <p className="mt-1">
                  {proposed.keptAs ? kept : <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" disabled={keeping || critique.stale} onClick={() => onKeepDecision(index)}>Keep as proposed decision</Button>}
                </p>
              </li>
            ))}
          </ul>
        </div>
      )}

      {sections.leftOut.length > 0 && (
        <details className="text-xs text-muted-foreground">
          <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">Left out of the answer ({sections.leftOut.length})</summary>
          <ul className="mt-1 list-disc space-y-0.5 pl-4">
            {sections.leftOut.map((item, i) => <li key={i}><span className="font-mono">{item.where}</span> {item.reason}</li>)}
          </ul>
        </details>
      )}

      <p className="font-mono text-[10px] text-muted-foreground">
        {critique.provider.kind} · {critique.provider.reportedModel ?? critique.provider.model} · {critique.findingsSent} findings sent
        {critique.findingsLeftOut > 0 ? ` · ${critique.findingsLeftOut} not sent` : ''}
        {critique.omittedPrivate ? ` · ${critique.omittedPrivate.count} private parts withheld` : ''}
      </p>
    </section>
  )
}

/** Asks the assistant to review the design. The deterministic findings do not need it. */
export function AssistantTab({ projectId, branchId, labels, focusTarget, onFocus }: Props) {
  const queryClient = useQueryClient()
  const [question, setQuestion] = useState('')
  const selectedElementId = useShellStore((s) => s.selectedElementId)
  const [focusSelection, setFocusSelection] = useState(true)
  const [critique, setCritique] = useState<Critique | null>(null)
  const [unavailable, setUnavailable] = useState<Unavailable | null>(null)
  const stored = useQuery({ queryKey: ['projects', projectId, 'review', 'critiques'], queryFn: () => reviewApi.critiques(projectId) })
  const ask = useMutation({
    mutationFn: () => reviewApi.critique(projectId, branchId, question.trim() || null, selectedElementId && focusSelection ? [selectedElementId] : []),
    onSuccess: (answer) => {
      if (isUnavailable(answer)) { setUnavailable(answer); setCritique(null) } else { setUnavailable(null); setCritique(answer) }
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review', 'critiques'] })
    },
    onError: shownInline,
  })
  // A stored answer is read from the server, which says whether the model changed since.
  const open = useMutation({
    mutationFn: (id: string) => reviewApi.storedCritique(projectId, id),
    onSuccess: (answer) => { setUnavailable(null); setCritique(answer) },
    onError: shownInline,
  })
  const mark = (change: (c: Critique) => Critique) => setCritique((c) => (c ? change(c) : c))
  const keepQuestion = useMutation({
    mutationFn: (index: number) => reviewApi.keepCritiqueQuestion(projectId, critique!.id, index),
    onSuccess: (kept, index) => {
      mark((c) => ({ ...c, questions: c.questions.map((q, i) => (i === index ? { ...q, keptAs: kept.id } : q)) }))
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'questions'] })
    },
    onError: shownInline,
  })
  const keepDecision = useMutation({
    mutationFn: (index: number) => reviewApi.keepCritiqueDecision(projectId, critique!.id, index),
    onSuccess: (kept, index) => {
      mark((c) => ({ ...c, proposedDecisions: c.proposedDecisions.map((d, i) => (i === index ? { ...d, keptAs: kept.id } : d)) }))
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'decisions'] })
    },
    // The model moved: the server refused. Show that the answer is stale.
    onError: (error) => { shownInline(); if (/changed/i.test(error.message)) mark((c) => ({ ...c, stale: true })) },
  })
  // A stored answer is a set of suggestions. Deleting one changes nothing that was kept from it.
  const remove = useMutation({
    mutationFn: (id: string) => reviewApi.deleteCritique(projectId, id),
    onSuccess: (_, id) => {
      setCritique((c) => (c?.id === id ? null : c))
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'review', 'critiques'] })
    },
    onError: shownInline,
  })
  const failure = ask.error ?? open.error ?? keepQuestion.error ?? keepDecision.error ?? remove.error

  return (
    <div className="space-y-3 p-3">
      <form className="space-y-1.5" onSubmit={(e) => { e.preventDefault(); ask.mutate() }}>
        <p className="text-xs leading-relaxed text-muted-foreground">
          The assistant reads the model and the findings, and gives questions, risks and proposed decisions. It sees names, not IDs. It cannot create a finding, settle one or accept a decision.
        </p>
        <label className="block text-[11px] font-medium text-muted-foreground">
          Your question (optional)
          <textarea className={areaClass} value={question} onChange={(e) => setQuestion(e.target.value)} maxLength={2000} placeholder="What happens when the broker is down?" />
        </label>
        {selectedElementId && (
          <label className="flex items-center gap-1.5 text-xs text-foreground">
            <input type="checkbox" checked={focusSelection} onChange={(e) => setFocusSelection(e.target.checked)} />
            Look at the selected element first
          </label>
        )}
        <Button type="submit" size="sm" className="gap-1.5" disabled={ask.isPending}>
          <Sparkles className="h-3.5 w-3.5" />
          {ask.isPending ? 'Asking…' : 'Ask the assistant'}
        </Button>
      </form>
      {failure && <p role="alert" className="text-xs text-destructive">{failure.message}</p>}
      {unavailable && <UnavailableNote state={unavailable} />}
      {critique && (
        <CritiquePanel
          critique={critique} focusTarget={focusTarget} onFocus={onFocus}
          keeping={keepQuestion.isPending || keepDecision.isPending}
          onKeepQuestion={(index) => keepQuestion.mutate(index)} onKeepDecision={(index) => keepDecision.mutate(index)}
        />
      )}

      <section aria-labelledby="review-stored-critiques">
        <h3 id="review-stored-critiques" className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Stored answers</h3>
        <p className="mt-1 text-xs leading-relaxed text-muted-foreground">The project keeps the newest answers of the assistant. Open one to read it again. An answer is a set of suggestions for the model version it was made for.</p>
        {stored.isError && <p role="alert" className="mt-1 text-xs text-destructive">{stored.error.message}</p>}
        {stored.isSuccess && stored.data.length === 0 && <p className="mt-1.5 text-[11px] text-muted-foreground">No answer is stored.</p>}
        <ul className="mt-1.5 space-y-1">
          {stored.data?.map((item) => (
            <li key={item.id} className="flex items-center gap-1">
              <button
                type="button" onClick={() => open.mutate(item.id)} disabled={open.isPending} aria-current={critique?.id === item.id ? 'true' : undefined}
                className={cn(
                  'flex min-w-0 flex-1 items-baseline gap-2 rounded-md border px-2 py-1 text-left text-[11px] hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary',
                  critique?.id === item.id ? 'border-primary' : 'border-border',
                )}
              >
                <span className="shrink-0 font-mono text-muted-foreground">{new Date(item.createdAt).toLocaleString()}</span>
                <span className="min-w-0 flex-1 truncate text-foreground">{item.question || 'General review'}</span>
                <span className="shrink-0 text-muted-foreground">{item.branchId ? 'draft or variant' : 'main model'}</span>
              </button>
              <Button
                variant="ghost" size="sm" className="h-6 w-6 shrink-0 p-0 text-muted-foreground" aria-label={`Delete the answer of ${new Date(item.createdAt).toLocaleString()}`}
                title="Delete this answer. Questions and decisions kept from it stay."
                disabled={remove.isPending}
                onClick={() => { if (window.confirm('Delete this stored answer? Questions and decisions you kept from it stay.')) remove.mutate(item.id) }}
              >
                <Trash2 className="h-3 w-3" />
              </Button>
            </li>
          ))}
        </ul>
      </section>

      <ProposalLists projectId={projectId} branchId={branchId} labels={labels} />
    </div>
  )
}
