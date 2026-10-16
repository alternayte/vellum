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

/** `ai` for a proposal of the assistant. For a submitted proposal: `agent-token:<id>` or `user:<id>`. */
export type ProposalOrigin = string

/** One line of a list of proposals. */
export interface ProposalSummary {
  id: string
  status: 'draft' | 'accepted' | 'rejected' | 'decided'
  name: string
  request: string
  origin: ProposalOrigin
  createdBy: string
  createdAt: string
  /** The user who made the last decision. */
  decidedBy: string | null
  decidedAt: string | null
  /** For additions: the number of items, and how many are accepted and rejected. Null for a flow. */
  items: number | null
  acceptedItems: number | null
  rejectedItems: number | null
}

export type ProposalKind = 'flow' | 'architecture'

/** A proposal of a list, with its kind. */
export interface PendingProposal extends ProposalSummary {
  kind: ProposalKind
}

/** One page of the proposals of both kinds, the newest first, and where each kind goes on. */
export interface ProposalPages {
  items: PendingProposal[]
  /** The cursor of the next page of each kind. Null when that kind has no more. */
  cursors: Record<ProposalKind, string | null>
}

const KINDS: ProposalKind[] = ['flow', 'architecture']

/**
 * Merges the pages of the two kinds. A kind with more pages hides the items that are older than
 * its last loaded item: an older item of that kind may still come, so the order stays true.
 */
export function mergeProposalPages(pages: Record<ProposalKind, { items: ProposalSummary[]; cursor: string | null }>): ProposalPages {
  const all = KINDS.flatMap((kind) => pages[kind].items.map((item) => ({ ...item, kind })))
  const frontier = KINDS
    .filter((kind) => pages[kind].cursor && pages[kind].items.length > 0)
    .map((kind) => pages[kind].items[pages[kind].items.length - 1].createdAt)
    .sort()
    .at(-1)
  return {
    items: all.filter((item) => !frontier || item.createdAt >= frontier).sort((a, b) => b.createdAt.localeCompare(a.createdAt)),
    cursors: { flow: pages.flow.cursor, architecture: pages.architecture.cursor },
  }
}

/** Who a proposal is from, for the sentence under its title. */
export function describeOrigin(proposal: { origin: ProposalOrigin; provider: { model: string } }): string {
  if (proposal.origin.startsWith('agent-token:')) return `Submitted by an agent with the token ${proposal.origin.slice('agent-token:'.length, 'agent-token:'.length + 8)}.`
  if (proposal.origin.startsWith('user:')) return 'Submitted over the API by a member of the workspace.'
  return `A suggestion from ${proposal.provider.model}.`
}

export interface FlowProposal {
  /** For a flow read from a document: the document, and `<source>@<revision>:path:start-end` of the lines it comes from. */
  document?: DocumentSource | null
  citation?: string | null
  id: string
  origin: ProposalOrigin
  status: 'draft' | 'accepted' | 'rejected'
  request: string
  name: string
  decidedBy: string | null
  decidedAt: string | null
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
  /** Who accepted or rejected the item, and when. Null while it is pending. */
  decidedBy?: string | null
  decidedAt?: string | null
  /** For an item read from a document: `<source>@<revision>:path:start-end`, the lines it comes from. */
  citation?: string | null
}

/** The document a proposal was read from. */
export interface DocumentSource {
  sourceId: string
  path: string
  citation: string
  lines: number
}

export interface ArchitectureProposal {
  /** Set when the proposal was read from a document of a Source. */
  document?: DocumentSource | null
  id: string
  origin: ProposalOrigin
  status: 'draft' | 'decided'
  request: string
  decidedBy: string | null
  decidedAt: string | null
  summary: string
  reasoningSummary: string
  openQuestions: string[]
  items: ArchitectureItem[]
  rejectedReferences: { where: string; alias: string; reason: string }[]
  provider: { kind: string; model: string; reportedModel: string | null }
  resolvedToExisting: { alias: string; id: string; name: string }[]
  stale: boolean
  /** Items that cannot be accepted now, with the reason. */
  itemIssues: ItemFailure[]
  omittedPrivate: { count: number; reason: string } | null
}

export interface ItemFailure {
  itemId: string
  reason: string
}

/**
 * The items a refused decision names, from a 409 `items_refused` answer of
 * `…/architecture-proposals/{id}/decide`. Null for any other error.
 */
export function refusedItems(error: unknown): ItemFailure[] | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null
  const body = error.body as { type?: unknown; failures?: unknown } | null
  return body?.type === 'items_refused' && Array.isArray(body.failures) ? (body.failures as ItemFailure[]) : null
}

export type ExplainTarget =
  | { kind: 'element' | 'operation' | 'channel' | 'subscription' | 'message' | 'scenario'; id: string }
  | { kind: 'trace-event'; scenarioId: string; sequence: number }

const base = (projectId: string) => `/api/projects/${projectId}/ai`

export const designApi = {
  /** A flow that a document of a Source describes. The flow cites the lines it comes from. */
  proposeFlowFromDocument: (projectId: string, sourceId: string, path: string, request: string) =>
    api<FlowProposal | Unavailable>('POST', `${base(projectId)}/flow-proposals`, {
      request, branchId: null, reviseScenarioId: null, documentSourceId: sourceId, documentPath: path,
    }),
  proposeFlow: (projectId: string, request: string, branchId?: string | null) =>
    api<FlowProposal | Unavailable>('POST', `${base(projectId)}/flow-proposals`, { request, branchId: branchId ?? null, reviseScenarioId: null }),
  acceptFlow: (projectId: string, id: string, acceptWithIssues: boolean) =>
    api<{ scenarioId: string }>('POST', `${base(projectId)}/flow-proposals/${id}/accept`, { name: null, acceptWithIssues }),
  getFlow: (projectId: string, id: string) => api<FlowProposal>('GET', `${base(projectId)}/flow-proposals/${id}`),
  getArchitecture: (projectId: string, id: string) => api<ArchitectureProposal>('GET', `${base(projectId)}/architecture-proposals/${id}`),
  /** The proposals that an agent or a caller of the API submitted and that nobody decided yet, the newest first. */
  pending: async (projectId: string): Promise<PendingProposal[]> => {
    const list = async (kind: PendingProposal['kind']) =>
      (await api<{ items: ProposalSummary[] }>('GET', `${base(projectId)}/${kind}-proposals?status=draft&submitted=true&limit=50`)).items.map((item) => ({ ...item, kind }))
    const [flows, additions] = await Promise.all([list('flow'), list('architecture')])
    return [...flows, ...additions].sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  },
  /**
   * Proposals of both kinds. `query` filters them (`mine`, `decided`, `submitted`, `status`).
   * `loaded` holds what earlier pages returned; the call adds the next page of each kind that has one.
   */
  proposals: async (projectId: string, query: string, loaded?: Record<ProposalKind, { items: ProposalSummary[]; cursor: string | null }>) => {
    const next = async (kind: ProposalKind) => {
      const before = loaded?.[kind]
      if (before && !before.cursor) return before
      const cursor = before?.cursor ? `&cursor=${encodeURIComponent(before.cursor)}` : ''
      const page = await api<{ items: ProposalSummary[]; cursor: string | null }>('GET', `${base(projectId)}/${kind}-proposals?${query}&limit=20${cursor}`)
      return { items: [...(before?.items ?? []), ...page.items], cursor: page.cursor }
    }
    const [flow, architecture] = await Promise.all([next('flow'), next('architecture')])
    return { flow, architecture }
  },
  /** Deletes a proposal nobody decided. A decided proposal, or additions with a decided item, stays as a record. */
  discard: (projectId: string, kind: ProposalKind, id: string) => api<void>('DELETE', `${base(projectId)}/${kind}-proposals/${id}`),
  rejectFlow: (projectId: string, id: string) => api<FlowProposal>('POST', `${base(projectId)}/flow-proposals/${id}/reject`),
  createMissing: (projectId: string, id: string, alias: string) =>
    api<FlowProposal>('POST', `${base(projectId)}/flow-proposals/${id}/missing/${encodeURIComponent(alias)}/create`),
  /** Prose of a Source through the AI path: inferred items, each citing its lines. */
  proposeFromDocument: (projectId: string, sourceId: string, path: string, request?: string) =>
    api<ArchitectureProposal | Unavailable>('POST', `${base(projectId)}/architecture-proposals/from-document`, { sourceId, path, request: request || null }),
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
