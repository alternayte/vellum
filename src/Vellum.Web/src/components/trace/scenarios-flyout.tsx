import { useState } from 'react'
import { Pencil, Play, Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useShellStore } from '@/stores/shell-store'
import { useTraceStore } from '@/stores/trace-store'
import { fetchScenario, useRunScenario, useScenarios, type ScenarioDocument } from '@/hooks/use-scenarios'
import { ScenarioEditor, type ScenarioReference } from './scenario-editor'

interface ScenariosFlyoutProps {
  projectId: string
  branchId?: string | null
  /** Every ID a scenario can reference, for the editor's reference list. */
  references: ScenarioReference[]
}

export function ScenariosFlyout({ projectId, branchId, references }: ScenariosFlyoutProps) {
  const { data: scenarios = [], isLoading } = useScenarios(projectId)
  const run = useRunScenario(projectId, branchId)
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const [editing, setEditing] = useState<{ scenario: ScenarioDocument; isNew: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const play = (scenarioId: string) => {
    setError(null)
    run.mutate(scenarioId, {
      onSuccess: (result) => {
        useTraceStore.getState().load(result, scenarioId)
        closeFlyout()
      },
      onError: (e) => setError(e.message),
    })
  }

  const edit = async (scenarioId: string) => {
    setError(null)
    try {
      setEditing({ scenario: await fetchScenario(projectId, scenarioId), isNew: false })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The scenario did not load.')
    }
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Scenarios</span>
        <Button
          variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs"
          onClick={() => setEditing({ scenario: { id: crypto.randomUUID(), name: '', steps: [], faults: [], assertions: [] }, isNew: true })}
        >
          <Plus className="h-3 w-3" />
          New
        </Button>
      </div>
      {error && <p role="alert" className="border-b border-border px-3 py-2 text-xs text-[var(--trace-failure)]">{error}</p>}
      <div className="flex-1 overflow-y-auto p-2">
        {!isLoading && scenarios.length === 0 && (
          <p className="px-1.5 py-1 text-xs leading-relaxed text-muted-foreground">
            A scenario is one behaviour of the system, such as placing an order. It names the calls, messages and writes in
            order, and can inject a failure. Play it to trace the flow on the canvas.
          </p>
        )}
        <ul className="space-y-0.5">
          {scenarios.map((scenario) => (
            <li key={scenario.id} className="flex items-center gap-1 rounded-md px-1.5 py-1 hover:bg-muted">
              <span className="min-w-0 flex-1 truncate text-sm text-foreground">{scenario.name}</span>
              <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => edit(scenario.id)} aria-label={`Edit ${scenario.name}`}>
                <Pencil className="h-3.5 w-3.5" />
              </Button>
              <Button
                variant="ghost" size="icon" className="h-7 w-7 text-[var(--trace-request)]"
                onClick={() => play(scenario.id)} disabled={run.isPending} aria-label={`Play ${scenario.name}`}
              >
                <Play className="h-3.5 w-3.5" />
              </Button>
            </li>
          ))}
        </ul>
      </div>
      {editing && (
        <ScenarioEditor
          key={editing.scenario.id}
          projectId={projectId}
          scenario={editing.scenario}
          isNew={editing.isNew}
          references={references}
          onClose={() => setEditing(null)}
          onPlay={(scenarioId) => { setEditing(null); play(scenarioId) }}
        />
      )}
    </div>
  )
}
