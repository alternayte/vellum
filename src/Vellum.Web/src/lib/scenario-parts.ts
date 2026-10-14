import { fetchScenario } from '@/hooks/use-scenarios'
import type { PromiseRevision } from '@/lib/promises-api'

/** What one step of a stored scenario names. */
export interface ScenarioStepParts {
  id: string
  type: string
  /** The elements the step runs in or against. */
  elementIds: string[]
  /** The operation, the channel and the subscriptions the step names: what a fault can target beside an element. */
  contractIds: string[]
  /** The message of a publish step. */
  messageId: string | null
  /** The element the step starts in: the caller, the publisher or the writer. */
  fromId: string | null
  /** What the step reaches: the operation of a call, the subscriptions of a publish, the store of a write. */
  targetIds: string[]
}

const text = (value: unknown): string | null => (typeof value === 'string' && value.length > 0 ? value : null)
const list = (value: unknown): unknown[] => (Array.isArray(value) ? value : [])

/** Every step of a scenario document, nested ones too, in the order they are written. */
export function scenarioSteps(steps: unknown[]): ScenarioStepParts[] {
  const found: ScenarioStepParts[] = []
  const walk = (items: unknown[]) => {
    for (const item of items) {
      if (!item || typeof item !== 'object') continue
      const step = item as Record<string, unknown>
      const id = text(step.id)
      const handlers = step.handlers && typeof step.handlers === 'object' ? (step.handlers as Record<string, unknown>) : {}
      if (id) {
        found.push({
          id,
          type: text(step.type) ?? '',
          elementIds: [step.from, step.by, step.store].map(text).filter((x): x is string => x !== null),
          contractIds: [step.operation, step.channel, ...Object.keys(handlers)].map(text).filter((x): x is string => x !== null),
          messageId: text(step.message),
          fromId: text(step.from) ?? text(step.by),
          targetIds: [step.operation, step.store, ...Object.keys(handlers)].map(text).filter((x): x is string => x !== null),
        })
      }
      walk(list(step.then))
      walk(list(step.onError))
      walk(list(step.else))
      for (const branch of list(step.branches)) walk(list(branch))
      for (const handler of Object.values(handlers)) walk(list(handler))
    }
  }
  walk(steps)
  return found
}

/**
 * The two ends of a step on the canvas: the element it starts in and the elements it
 * reaches. A contract (an operation, a subscription) shows as the element that owns it.
 */
export function stepEnds(step: ScenarioStepParts, owners: Record<string, string>): { fromId: string | null; toIds: string[] } {
  return { fromId: step.fromId, toIds: unique(step.targetIds.map((id) => owners[id] ?? id)) }
}

/**
 * The elements to show on the canvas for a scenario: those its steps name, and the owners
 * of the contracts its steps name. `stepIds` limits it to those steps. `owners` gives the
 * element that owns an operation, a subscription or a message.
 */
export async function scenarioElementIds(projectId: string, scenarioId: string, stepIds: string[] | null, owners: Record<string, string>): Promise<string[]> {
  const scenario = await fetchScenario(projectId, scenarioId)
  const steps = scenarioSteps(scenario.steps).filter((s) => stepIds === null || stepIds.includes(s.id))
  return unique(steps.flatMap((s) => [...s.elementIds, ...[...s.contractIds, s.messageId ?? ''].map((id) => owners[id])]))
}

/** The elements to show for a promise: what it links, and the flows it links or checks. */
export async function promiseElementIds(projectId: string, revision: PromiseRevision, owners: Record<string, string>): Promise<string[]> {
  const { links } = revision
  const scenarioIds = unique([...links.scenarioIds, ...revision.obligations.map((o) => o.recipe.scenarioCheck?.scenarioId)])
  // A scenario that no longer exists shows nothing here. The promise dialog reports it as a missing link.
  const fromFlows = await Promise.all(scenarioIds.map((id) => scenarioElementIds(projectId, id, null, owners).catch(() => [] as string[])))
  return unique([
    ...links.elementIds,
    ...[...links.operationIds, ...links.subscriptionIds, ...links.messageIds].map((id) => owners[id]),
    ...fromFlows.flat(),
  ])
}

function unique(ids: (string | null | undefined)[]): string[] {
  return [...new Set(ids.filter((id): id is string => typeof id === 'string' && id.length > 0))]
}
