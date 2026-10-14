import { api } from '@/lib/settings-api'

export type ScanStatus = 'queued' | 'running' | 'waiting' | 'completed' | 'failed' | 'cancelled'

/** How many accepted facts this scan could judge, and why it could not judge the rest. */
export interface LinkedFactCoverage {
  covered: number
  notCovered: number
  scope: number
  limit: number
  withheld: number
  failed: number
}

/** A pull request as GitHub described it when the scan pinned its head commit. The title is repository text: show it as text. */
export interface ScanPullRequest {
  number: number
  title: string | null
  state: string
  merged: boolean
  baseRef: string
  baseSha: string
  headRef: string
  headSha: string
  headRepository: string | null
  /** The head is in another repository. The scan read the head commit through this one. */
  fromFork: boolean
}

export interface ScanSummary {
  id: string
  repository: string
  /** A tracking scan follows the ref the model was accepted from. A comparison scan is a preview. */
  kind: 'tracking' | 'comparison'
  trackedRef: string | null
  canDecide: boolean
  canDecideReason: string | null
  coverage: { filesListed: number; filesExtracted: number; linkedFacts: LinkedFactCoverage | null } | null
  relinked: number
  needsReview: number
  ref: string | null
  commitSha: string | null
  status: ScanStatus
  isComplete: boolean
  progress: { filesListed: number; filesRead: number; filesSkipped: number; bytesRead: number }
  proposalCounts: { pending: number; accepted: number; rejected: number }
  error: { code: string; message: string } | null
  retryAt: string | null
  requestedBy: string
  createdAt: string
  completedAt: string | null
  /** The pull request the scan was asked for. Null for a scan of a ref or a commit. */
  pullRequestNumber: number | null
  /** Null until the scan read the pull request. */
  pullRequest: ScanPullRequest | null
}

export interface ScanNote {
  code: string
  path: string | null
  line: number | null
  message: string
}

export interface ScanDetail extends ScanSummary {
  notes: ScanNote[]
  ruleVersions: { rule: string; version: number }[]
}

export type ProposalAction = 'add' | 'change' | 'remove' | 'conflict' | 'relink'

export interface ProposalImpact {
  scenarios: { id: string; name: string; stepIds: string[] }[]
  entities: { entityKind: string; id: string; name: string }[]
}
export type ProposalStatus = 'pending' | 'accepted' | 'rejected' | 'superseded'

export interface ProposalEvidence {
  path: string
  startLine: number
  endLine: number
  rule: string
  pointer: string
  url: string
}

export interface Proposal {
  id: string
  action: ProposalAction
  entityKind: string
  entityId: string
  factKey: string
  name: string
  proposed: Record<string, unknown> | null
  current: Record<string, unknown> | null
  changes: { field: string; from: unknown; to: unknown }[]
  evidence: ProposalEvidence[]
  dependsOn: string[]
  status: ProposalStatus
  partial: boolean
  canDecide: boolean
  canDecideReason: string | null
  /** What an accepted removal or change breaks. Accepting needs an explicit acknowledgement. */
  impact: ProposalImpact | null
  /** Large bodies are cut in the list. `importApi.proposal` returns the whole item. */
  truncated: boolean
}

export interface DecisionResult {
  accepted: string[]
  rejected: string[]
  failed: { id: string; reason: string }[]
}

export interface SourceWindow {
  path: string
  commitSha: string
  startLine: number
  endLine: number
  lines: string[]
}

export type ImpactCertainty = 'definite' | 'possible' | 'unknown'

/** One thing the source of a scan changes: a proposal, or a source pattern that the base snapshot and this one do not both hold. */
export interface ImpactChange {
  id: string
  kind: 'contract' | 'source-pattern'
  action: ProposalAction
  entityKind: string
  entityId: string | null
  name: string
  certainty: ImpactCertainty
  /** What the certainty stands on. */
  basis: string
  diff: { field: string; from: string | null; to: string | null }[]
  evidence: ProposalEvidence[]
  /** The element that owns the changed thing, when the model has one. */
  elementId: string | null
  pattern: string | null
}

export interface ImpactReason {
  certainty: ImpactCertainty
  changeId: string
  text: string
}

/** One affected step of a flow, in the order the scenario writes it. */
export interface ImpactStep {
  stepId: string
  kind: 'call' | 'publish' | 'write' | 'parallel' | 'branch' | 'assertion' | 'fault' | 'step'
  /** What the step calls, publishes or writes to. */
  label: string | null
  /** `definite` only when a reason stands on a contract diff of what the step names. */
  certainty: ImpactCertainty
  reasons: ImpactReason[]
}

export interface ImpactFlow {
  scenarioId: string
  name: string
  stepIds: string[]
  certainty: ImpactCertainty
  reasons: ImpactReason[]
  steps: ImpactStep[]
}

export interface ImpactPromise {
  id: string
  requirementId: string
  title: string
  state: 'draft' | 'approved'
  revision: number
  /** `broken` only when a deterministic check failed against this scan. */
  standing: 'broken' | 'at-risk' | 'possible' | 'unknown'
  certainty: ImpactCertainty
  reasons: ImpactReason[]
  /** The source pattern checks of the promise, with their result against this scan. */
  checks: { obligationId: string; key: string; title: string; result: 'passed' | 'failed' | 'not-run' | 'unsupported'; detail: string | null }[]
}

export interface ChangeImpact {
  scanId: string
  repository: string
  kind: 'tracking' | 'comparison'
  base: { scanId: string | null; ref: string | null; commitSha: string | null }
  head: { scanId: string | null; ref: string | null; commitSha: string | null }
  limits: string[]
  changes: ImpactChange[]
  flows: ImpactFlow[]
  promises: ImpactPromise[]
  promisesNotAffected: number
  pullRequest: ScanPullRequest | null
}

interface Page<T> {
  items: T[]
  cursor: string | null
}

async function allPages<T>(url: string): Promise<T[]> {
  const items: T[] = []
  let cursor: string | null = null
  do {
    const separator = url.includes('?') ? '&' : '?'
    const page: Page<T> = await api<Page<T>>('GET', `${url}${separator}limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
    items.push(...page.items)
    cursor = page.cursor
  } while (cursor)
  return items
}

const base = (projectId: string) => `/api/projects/${projectId}/imports`

export const importApi = {
  list: (projectId: string) => allPages<ScanSummary>(base(projectId)),
  get: (projectId: string, id: string) => api<ScanDetail>('GET', `${base(projectId)}/${id}`),
  start: (projectId: string, body: { repository: string; ref?: string; pullRequest?: number; includePaths?: string[]; excludePaths?: string[] }) =>
    api<{ id: string }>('POST', base(projectId), body),
  cancel: (projectId: string, id: string) => api<void>('POST', `${base(projectId)}/${id}/cancel`),
  resume: (projectId: string, id: string) => api<void>('POST', `${base(projectId)}/${id}/resume`),
  proposals: (projectId: string, id: string) => allPages<Proposal>(`${base(projectId)}/${id}/proposals`),
  proposal: (projectId: string, id: string, proposalId: string) => api<Proposal>('GET', `${base(projectId)}/${id}/proposals/${proposalId}`),
  decide: (projectId: string, id: string, decision: { accept: string[]; reject: string[]; acknowledgeImpact: boolean; relink: { id: string; factKey: string | null }[] }) =>
    api<DecisionResult>('POST', `${base(projectId)}/${id}/proposals/decide`, decision),
  impact: (projectId: string, id: string) => api<ChangeImpact>('GET', `${base(projectId)}/${id}/impact`),
  track: (projectId: string, id: string) => api<ScanDetail>('POST', `${base(projectId)}/${id}/track`),
  source: (projectId: string, id: string, evidence: Pick<ProposalEvidence, 'path' | 'startLine' | 'endLine'>) =>
    api<SourceWindow>(
      'GET',
      `${base(projectId)}/${id}/source?path=${encodeURIComponent(evidence.path)}&startLine=${evidence.startLine}&endLine=${evidence.endLine}`,
    ),
}

/** What a scan read, in words: the pull request, the ref, or the commit. */
export function scanSubject(scan: Pick<ScanSummary, 'pullRequestNumber' | 'pullRequest' | 'ref' | 'trackedRef' | 'commitSha'>): string {
  if (scan.pullRequestNumber !== null) return `pull request #${scan.pullRequestNumber}`
  return scan.ref ?? scan.trackedRef ?? (scan.commitSha ? scan.commitSha.slice(0, 10) : 'default branch')
}

/** The state of a pull request in one word. A merged one is closed and says merged. */
export function pullRequestState(pull: ScanPullRequest): string {
  return pull.merged ? 'merged' : pull.state
}

export function isActive(status: ScanStatus): boolean {
  return status === 'queued' || status === 'running' || status === 'waiting'
}

/** One sentence for the state of a scan. A partial scan is never called complete. */
/** Why some accepted facts were not judged, in words. Empty when all were. */
export function describeNotCovered(coverage: LinkedFactCoverage | null | undefined): string | null {
  if (!coverage || coverage.notCovered === 0) return null
  const reasons = [
    coverage.scope > 0 && `${coverage.scope} outside the paths of this scan`,
    coverage.limit > 0 && `${coverage.limit} past a limit`,
    coverage.withheld > 0 && `${coverage.withheld} in files that Vellum does not read`,
    coverage.failed > 0 && `${coverage.failed} in files that failed`,
  ].filter(Boolean)
  return `This scan did not look at the source of ${coverage.notCovered} accepted ${coverage.notCovered === 1 ? 'fact' : 'facts'} (${reasons.join(', ')}). It says nothing about them.`
}

export function describeScan(scan: ScanSummary): string {
  switch (scan.status) {
    case 'queued':
      return 'Waiting to start'
    case 'running':
      return scan.progress.filesListed === 0 ? 'Listing files' : `Reading files: ${scan.progress.filesRead} of ${scan.progress.filesListed}`
    case 'waiting':
      return scan.retryAt ? `Paused by the GitHub rate limit until ${new Date(scan.retryAt).toLocaleTimeString()}` : 'Paused by the GitHub rate limit'
    case 'completed':
      return scan.isComplete ? 'Scan complete' : 'Partial scan: a limit stopped it before every file was read'
    case 'failed':
      return scan.error?.message ?? 'The scan failed'
    case 'cancelled':
      return 'Cancelled'
  }
}

/** Accepting a proposal also needs everything it depends on, unless that is accepted already. */
export function withDependencies(selected: Set<string>, proposals: Proposal[]): Set<string> {
  const byId = new Map(proposals.map((p) => [p.id, p]))
  const result = new Set(selected)
  const queue = [...selected]
  while (queue.length > 0) {
    const proposal = byId.get(queue.pop()!)
    for (const dependency of proposal?.dependsOn ?? []) {
      if (!result.has(dependency) && byId.get(dependency)?.status === 'pending') {
        result.add(dependency)
        queue.push(dependency)
      }
    }
  }
  return result
}
