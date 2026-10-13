import { ApiError, api } from '@/lib/settings-api'

/** AI is off, over budget, rate limited, or the generation failed. A state, not a transport error. */
export interface Unavailable {
  status: 'unavailable' | 'failed'
  reason: { code: string; message: string; setting: string | null; retryAfterSeconds: number | null }
}

export function isUnavailable(value: unknown): value is Unavailable {
  const status = (value as { status?: unknown } | null)?.status
  return (status === 'unavailable' || status === 'failed') && 'reason' in (value as object)
}

interface Ref {
  alias: string
  id: string | null
  name: string
}

export interface MissingEntry {
  alias: string
  kind: 'element' | 'operation' | 'channel' | 'subscription' | 'message'
  name: string
  elementKind: string | null
  transport: string | null
  channelKind: string | null
  parent: Ref | null
  owner: Ref | null
  channel: Ref | null
  consumer: Ref | null
  reason: string
  confidence: number
  createdId: string | null
  createAfter: string[]
}

export interface FlowStep {
  type: 'call' | 'publish' | 'write' | 'parallel' | 'branch'
  id: string
  [field: string]: unknown
}

export interface FlowProposal {
  id: string
  status: 'draft' | 'accepted' | 'rejected'
  request: string
  name: string
  summary: string
  reasoningSummary: string
  openQuestions: string[]
  scenario: { id: string; name: string; steps: FlowStep[]; faults: unknown[]; assertions: unknown[] }
  blockedSteps: { stepId: string; needs: string[] }[]
  missing: MissingEntry[]
  rejectedReferences: { where: string; alias: string; reason: string }[]
  issues: { stepId: string; code: string; message: string }[]
  preview: { hash: string; eventCount: number; outcomes: Record<string, string> } | null
  diff: { addedSteps: string[]; removedSteps: string[]; changedSteps: string[] } | null
  provider: { kind: string; model: string; reportedModel: string | null }
  usage: { inputTokens: number | null; outputTokens: number | null; costUsd: number | null }
  createdAt: string
  /** Things the answer asked for that the model already holds. Vellum used the existing ones. */
  resolvedToExisting: { alias: string; id: string; name: string }[]
  /** The model changed after the proposal was made. The steps and problems shown are current. */
  stale: boolean
  omittedPrivate: { count: number; reason: string } | null
}

export interface ArchitectureItem {
  id: string
  alias: string
  action: 'add'
  entityKind: 'element' | 'relationship' | 'operation' | 'channel' | 'subscription' | 'binding' | 'message'
  name: string
  proposed: Record<string, unknown>
  dependsOn: string[]
  confidence: number
  rationale: string
  status: 'pending' | 'accepted' | 'rejected'
}

export interface ArchitectureProposal {
  id: string
  status: 'draft' | 'decided'
  request: string
  summary: string
  reasoningSummary: string
  openQuestions: string[]
  items: ArchitectureItem[]
  rejectedReferences: { where: string; alias: string; reason: string }[]
  provider: { kind: string; model: string; reportedModel: string | null }
  resolvedToExisting: { alias: string; id: string; name: string }[]
  stale: boolean
  /** Items that cannot be accepted now, with the reason. */
  itemIssues: { itemId: string; reason: string }[]
  omittedPrivate: { count: number; reason: string } | null
}

export type ExplainTarget =
  | { kind: 'element' | 'operation' | 'channel' | 'subscription' | 'message' | 'scenario'; id: string }
  | { kind: 'trace-event'; scenarioId: string; sequence: number }

const base = (projectId: string) => `/api/projects/${projectId}/ai`

export const designApi = {
  proposeFlow: (projectId: string, request: string, branchId?: string | null) =>
    api<FlowProposal | Unavailable>('POST', `${base(projectId)}/flow-proposals`, { request, branchId: branchId ?? null, reviseScenarioId: null }),
  acceptFlow: (projectId: string, id: string, acceptWithIssues: boolean) =>
    api<{ scenarioId: string }>('POST', `${base(projectId)}/flow-proposals/${id}/accept`, { name: null, acceptWithIssues }),
  getFlow: (projectId: string, id: string) => api<FlowProposal>('GET', `${base(projectId)}/flow-proposals/${id}`),
  rejectFlow: (projectId: string, id: string) => api<FlowProposal>('POST', `${base(projectId)}/flow-proposals/${id}/reject`),
  createMissing: (projectId: string, id: string, alias: string) =>
    api<FlowProposal>('POST', `${base(projectId)}/flow-proposals/${id}/missing/${encodeURIComponent(alias)}/create`),
  proposeArchitecture: (projectId: string, request: string, branchId?: string | null) =>
    api<ArchitectureProposal | Unavailable>('POST', `${base(projectId)}/architecture-proposals`, { request, branchId: branchId ?? null }),
  decideArchitecture: (projectId: string, id: string, accept: string[], reject: string[]) =>
    api<ArchitectureProposal>('POST', `${base(projectId)}/architecture-proposals/${id}/decide`, { accept, reject }),
}

/**
 * Streams an explanation. `onDelta` gets each piece of text as it arrives. Resolves with
 * `null` when the stream completed, or with the reason when AI is not available.
 */
export async function explain(
  projectId: string,
  target: ExplainTarget,
  question: string | null,
  branchId: string | null | undefined,
  onDelta: (text: string) => void,
  signal: AbortSignal,
  onNotice?: (omittedPrivate: number) => void,
): Promise<Unavailable | null> {
  const body = {
    target: {
      kind: target.kind,
      id: 'id' in target ? target.id : null,
      scenarioId: 'scenarioId' in target ? target.scenarioId : null,
      sequence: 'sequence' in target ? target.sequence : null,
    },
    question,
    branchId: branchId ?? null,
  }
  const response = await fetch(`${base(projectId)}/explain`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  })
  if (!response.ok) {
    const payload = (await response.json().catch(() => null)) as { title?: string } | null
    throw new ApiError(response.status, payload?.title ?? 'The explanation did not start.')
  }
  if (!response.headers.get('content-type')?.includes('text/event-stream')) {
    const payload: unknown = await response.json()
    if (isUnavailable(payload)) return payload
    throw new ApiError(response.status, 'The explanation did not start.')
  }

  const reader = response.body!.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  for (;;) {
    const { done, value } = await reader.read()
    if (done) return null
    buffer += decoder.decode(value, { stream: true })
    let end: number
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)
      for (const line of frame.split('\n')) {
        if (!line.startsWith('data:')) continue
        const event = JSON.parse(line.slice(5)) as { delta?: string; done?: boolean; error?: { message: string }; notice?: { omittedPrivate?: number } }
        if (event.notice?.omittedPrivate) onNotice?.(event.notice.omittedPrivate)
        if (event.delta) onDelta(event.delta)
        if (event.error) throw new ApiError(502, event.error.message)
        if (event.done) return null
      }
    }
  }
}

/** One line for a proposed step, with names in place of IDs. */
export function describeStep(step: FlowStep, labels: Record<string, string>): string {
  const name = (id: unknown) => (typeof id === 'string' ? (labels[id] ?? 'an unknown part') : 'an unknown part')
  switch (step.type) {
    case 'call':
      return `${name(step.from)} calls ${name(step.operation)}`
    case 'publish':
      return `${name(step.from)} publishes ${name(step.message)} to ${name(step.channel)}${typeof step.routingKey === 'string' && step.routingKey ? ` with routing key ${step.routingKey}` : ''}`
    case 'write':
      return `${name(step.by)} writes ${typeof step.dataset === 'string' ? step.dataset : 'data'} in ${name(step.store)}`
    case 'parallel':
      return 'In parallel'
    case 'branch':
      return `If step ${String(step.whenStep)} ${String(step.is).toLowerCase()}`
  }
}

/** The nested step lists of a step, with a label for each. */
export function childLists(step: FlowStep): { label: string; steps: FlowStep[] }[] {
  const list = (value: unknown): FlowStep[] => (Array.isArray(value) ? (value as FlowStep[]) : [])
  switch (step.type) {
    case 'call':
      return [{ label: 'then', steps: list(step.then) }, { label: 'on error', steps: list(step.onError) }]
    case 'publish':
      return Object.entries((step.handlers as Record<string, unknown>) ?? {}).map(([subscription, steps]) => ({ label: `consumer ${subscription}`, steps: list(steps) }))
    case 'parallel':
      return list(step.branches).map((branch, i) => ({ label: `branch ${i + 1}`, steps: list(branch) }))
    case 'branch':
      return [{ label: 'then', steps: list(step.then) }, { label: 'else', steps: list(step.else) }]
    default:
      return []
  }
}
