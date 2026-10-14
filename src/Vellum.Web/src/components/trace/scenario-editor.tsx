import { useCallback, useMemo, useState, type KeyboardEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { useDeleteScenario, useRunScenario, useSaveScenario, type ScenarioDocument } from '@/hooks/use-scenarios'
import { allStepIds, bodyText, childLists, readBody, renameStep, type ScenarioBody, type Step } from '@/lib/scenario-document'
import type { TraceIssue, TraceRun } from '@/lib/trace'
import { cn } from '@/lib/utils'
import { CatalogContext, StepEditingContext, describeStep, useScenarioCatalog, type StepEditing } from './scenario-catalog'
import { AssertionsEditor, FaultsEditor } from './scenario-checks'
import { StepList } from './scenario-steps'

export interface ScenarioReference {
  group: 'Elements' | 'Operations' | 'Channels' | 'Subscriptions' | 'Messages'
  id: string
  label: string
  /** The kind of an element: actor, system, app, store. */
  kind?: string
}

interface ScenarioEditorProps {
  projectId: string
  branchId?: string | null
  scenario: ScenarioDocument
  isNew: boolean
  references: ScenarioReference[]
  onClose: () => void
  /** The scenario was saved and played without a problem. */
  onPlayed: (scenarioId: string, run: TraceRun) => void
}

type Tab = 'steps' | 'json'

const GUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi

/**
 * Edits a scenario as steps chosen from the model by name, with a JSON view of the same
 * document. The server stays the authority: Save stores the document and then plays it once,
 * and the problems it reports are shown at the step they belong to.
 */
export function ScenarioEditor({ projectId, branchId, scenario, isNew, references, onClose, onPlayed }: ScenarioEditorProps) {
  const catalog = useScenarioCatalog(projectId, branchId, references)
  const save = useSaveScenario(projectId)
  const run = useRunScenario(projectId, branchId)
  const remove = useDeleteScenario(projectId)

  const [name, setName] = useState(scenario.name)
  const initial = useMemo(() => readBody(scenario), [scenario])
  const [body, setBody] = useState<ScenarioBody>(initial.body)
  // A stored document with a part the editor does not know opens as JSON, so nothing is lost.
  const [tab, setTab] = useState<Tab>(initial.unknown.length > 0 ? 'json' : 'steps')
  const [jsonText, setJsonText] = useState(() =>
    initial.unknown.length > 0 ? JSON.stringify({ steps: scenario.steps, faults: scenario.faults, assertions: scenario.assertions }, null, 2) : '')
  const [jsonError, setJsonError] = useState<string | null>(initial.unknown.length > 0 ? unknownText(initial.unknown) : null)
  const [invalidJson, setInvalidJson] = useState<Set<string>>(() => new Set())
  const [issues, setIssues] = useState<TraceIssue[]>([])
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)

  const reportJson = useCallback((key: string, invalid: boolean) => {
    setInvalidJson((current) => {
      if (current.has(key) === invalid) return current
      const next = new Set(current)
      if (invalid) next.add(key)
      else next.delete(key)
      return next
    })
  }, [])

  const stepIds = useMemo(() => allStepIds(body.steps), [body.steps])
  // A thread names a step of the stored scenario, so only saved steps take comments.
  const savedStepIds = useMemo(() => (isNew ? [] : allStepIds(initial.body.steps)), [isNew, initial])
  const stepsById = useMemo(() => {
    const map = new Map<string, Step>()
    const walk = (steps: Step[]) => steps.forEach((s) => { map.set(s.id, s); childLists(s).forEach((l) => walk(l.steps)) })
    walk(body.steps)
    return map
  }, [body.steps])
  const stepSummary = useCallback((id: string) => {
    const step = stepsById.get(id)
    return step ? describeStep(step, catalog) : undefined
  }, [stepsById, catalog])

  // The server names parts by ID. The editor names them as the model does.
  const named = useCallback((text: string) => text.replace(GUID, (id) => catalog.name(id.toLowerCase()) ?? catalog.name(id) ?? id), [catalog])
  const byStep = useMemo(() => {
    const map: Record<string, string[]> = {}
    for (const issue of issues) if (issue.stepId && stepIds.includes(issue.stepId)) (map[issue.stepId] ??= []).push(named(issue.message))
    return map
  }, [issues, stepIds, named])
  const byAssertion = useMemo(() => {
    const map: Record<string, string[]> = {}
    for (const issue of issues) {
      const owner = body.assertions.find((a) => issue.message.startsWith(`Assertion ${a.id} `) || issue.message.includes(`Assertion ID '${a.id}'`))
      if (owner) (map[owner.id] ??= []).push(named(issue.message))
    }
    return map
  }, [issues, body.assertions, named])
  const general = issues.filter((issue) =>
    !(issue.stepId && stepIds.includes(issue.stepId)) && !Object.values(byAssertion).flat().includes(named(issue.message)))

  const editing = useMemo<StepEditing>(() => ({
    allIds: stepIds,
    issues: byStep,
    reportJson,
    stepSummary,
    rename: (from, to) => {
      if (!to) return 'A step needs an ID.'
      if (stepIds.includes(to)) return `Step ID ${to} is in use.`
      if ([...to].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127)) return 'A step ID is one line of text.'
      setBody((current) => renameStep(current, from, to))
      return null
    },
    comments: isNew ? null : { projectId, scenarioId: scenario.id, scenarioName: scenario.name, savedStepIds },
  }), [stepIds, byStep, reportJson, stepSummary, isNew, projectId, scenario.id, scenario.name, savedStepIds])

  const change = (next: ScenarioBody) => {
    setBody(next)
    setStatus(null)
  }

  const showTab = (next: Tab) => {
    if (next === tab) return
    if (next === 'json') {
      setJsonText(bodyText(body))
      setJsonError(null)
    }
    setTab(next)
  }

  const editJson = (text: string) => {
    setJsonText(text)
    let raw: unknown
    try {
      raw = JSON.parse(text)
    } catch (e) {
      setJsonError(`The document is not valid JSON. ${e instanceof Error ? e.message : ''}`)
      return
    }
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      setJsonError('The document is an object with steps, faults and assertions.')
      return
    }
    const read = readBody(raw as Record<string, unknown>)
    if (read.unknown.length > 0) {
      setJsonError(unknownText(read.unknown))
      return
    }
    setJsonError(null)
    setBody(read.body)
  }

  const submit = async (thenPlay: boolean) => {
    if (!name.trim()) return setError('Give the scenario a name.')
    if (jsonError) return setError('Fix the JSON document first.')
    if (invalidJson.size > 0) return setError(`Fix the JSON in ${invalidJson.size === 1 ? 'one payload field' : `${invalidJson.size} payload fields`} first.`)
    setError(null)
    setStatus(null)
    try {
      await save.mutateAsync({ isNew, scenario: { id: scenario.id, name: name.trim(), ...body } })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The scenario was not saved.')
      return
    }
    // The server checks the references, payloads and schemas when the scenario plays.
    let result: TraceRun
    try {
      result = await run.mutateAsync(scenario.id)
    } catch (e) {
      setError(`Saved. The check did not run: ${e instanceof Error ? e.message : 'unknown error'}`)
      return
    }
    setIssues(result.issues)
    if (result.issues.length > 0) {
      setStatus(`Saved. The scenario does not play yet: ${result.issues.length === 1 ? 'one problem' : `${result.issues.length} problems`} to fix.`)
      if (tab === 'json') setTab('steps')
      return
    }
    if (thenPlay) onPlayed(scenario.id, result)
    else onClose()
  }

  const pending = save.isPending || run.isPending
  const onTabKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
    e.preventDefault()
    const next: Tab = tab === 'steps' ? 'json' : 'steps'
    if (next === 'json' && invalidJson.size > 0) return
    showTab(next)
    document.getElementById(`scenario-tab-${next}`)?.focus()
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-5xl">
        <DialogHeader className="pr-8">
          <DialogTitle>{isNew ? 'New scenario' : 'Edit scenario'}</DialogTitle>
          <DialogDescription>
            A scenario is one behaviour, step by step. Choose each part from the model. The first step is the trigger.
            Save plays the scenario once and shows what does not fit the model.
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-wrap items-end gap-3">
          <label className="block min-w-60 flex-1 text-xs font-medium text-muted-foreground">
            Name
            <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="Place an order" maxLength={200} data-autofocus />
          </label>
          <div role="tablist" aria-label="Scenario view" className="flex rounded-lg border border-border p-0.5" onKeyDown={onTabKey}>
            {(['steps', 'json'] as Tab[]).map((t) => {
              const blocked = t === 'json' ? invalidJson.size > 0 : !!jsonError && tab === 'json'
              return (
                <button
                  key={t}
                  id={`scenario-tab-${t}`}
                  type="button"
                  role="tab"
                  aria-selected={tab === t}
                  aria-controls={`scenario-panel-${t}`}
                  tabIndex={tab === t ? 0 : -1}
                  disabled={blocked}
                  title={blocked ? (t === 'json' ? 'Fix the JSON in the payload fields first.' : 'Fix the JSON document first.') : undefined}
                  onClick={() => showTab(t)}
                  className={cn(
                    'rounded-md px-3 py-1 text-xs font-medium outline-none focus-visible:ring-2 focus-visible:ring-ring/60 disabled:opacity-50',
                    tab === t ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground',
                  )}
                >
                  {t === 'steps' ? 'Steps' : 'JSON'}
                </button>
              )
            })}
          </div>
        </div>

        <div className="-mx-4 min-h-0 flex-1 overflow-y-auto px-4">
          {tab === 'steps' ? (
            <div id="scenario-panel-steps" role="tabpanel" aria-labelledby="scenario-tab-steps" className="space-y-5 pb-1">
              {general.length > 0 && (
                <div role="alert" className="rounded-lg border border-[var(--trace-failure)] bg-[color-mix(in_srgb,var(--trace-failure)_8%,transparent)] p-2.5">
                  <p className="text-xs font-medium text-foreground">The server found these problems</p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-[11px] text-foreground">
                    {general.map((issue, i) => <li key={i}>{named(issue.message)}{issue.stepId && !stepIds.includes(issue.stepId) ? ` (step ${issue.stepId})` : ''}</li>)}
                  </ul>
                </div>
              )}
              <CatalogContext.Provider value={catalog}>
                <StepEditingContext.Provider value={editing}>
                  <section aria-labelledby="scenario-steps-title">
                    <h3 id="scenario-steps-title" className="mb-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">Steps <span className="font-mono">{stepIds.length}</span></h3>
                    <StepList
                      steps={body.steps} onChange={(steps) => change({ ...body, steps })} inherited={[]} first="Trigger"
                      empty="Add the step that starts the behaviour, such as a call from the customer."
                    />
                  </section>
                  <section aria-labelledby="scenario-faults-title">
                    <h3 id="scenario-faults-title" className="mb-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">Failures to inject <span className="font-mono">{body.faults.length}</span></h3>
                    <FaultsEditor faults={body.faults} onChange={(faults) => change({ ...body, faults })} />
                  </section>
                  <section aria-labelledby="scenario-results-title">
                    <h3 id="scenario-results-title" className="mb-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">Expected results <span className="font-mono">{body.assertions.length}</span></h3>
                    <AssertionsEditor
                      assertions={body.assertions} stepIds={stepIds} stepSummary={stepSummary} results={byAssertion}
                      onChange={(assertions) => change({ ...body, assertions })}
                    />
                  </section>
                </StepEditingContext.Provider>
              </CatalogContext.Provider>
            </div>
          ) : (
            <div id="scenario-panel-json" role="tabpanel" aria-labelledby="scenario-tab-json" className="flex h-full flex-col gap-1.5">
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                The same document as the server stores it. A change here shows in Steps. Parts are named by ID; the format is in the documentation under Reference, Scenario format.
              </p>
              <label htmlFor="scenario-json" className="sr-only">Scenario document</label>
              <textarea
                id="scenario-json"
                value={jsonText}
                onChange={(e) => editJson(e.target.value)}
                spellCheck={false}
                aria-invalid={jsonError ? true : undefined}
                aria-describedby={jsonError ? 'scenario-json-error' : undefined}
                className="min-h-[50vh] flex-1 resize-none rounded-lg border border-input bg-background p-3 font-mono text-xs leading-relaxed text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive"
              />
              {jsonError && <p id="scenario-json-error" role="alert" className="text-xs text-[var(--trace-failure)]">{jsonError}</p>}
            </div>
          )}
        </div>

        {status && <p role="status" className="text-xs text-foreground">{status}</p>}
        {error && <p role="alert" className="text-xs text-[var(--trace-failure)]">{error}</p>}

        <div className="flex flex-wrap items-center gap-2">
          {!isNew && (
            <Button
              variant="ghost" size="sm" className="text-[var(--trace-failure)]"
              disabled={remove.isPending}
              onClick={() => remove.mutate(scenario.id, { onSuccess: onClose, onError: (e) => setError(e.message) })}
            >
              Delete
            </Button>
          )}
          <div className="flex-1" />
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button variant="secondary" size="sm" disabled={pending} onClick={() => submit(false)}>Save</Button>
          <Button size="sm" disabled={pending} onClick={() => submit(true)}>Save and play</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function unknownText(unknown: string[]): string {
  return `The editor cannot read ${unknown.join('; ')}. Step types are call, publish, write, parallel and branch; expected results are outcome and event-count.`
}
