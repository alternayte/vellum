import { ApiError, api, apiError } from '@/lib/settings-api'

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
  /** True for a failed or cancelled scan that a resume can carry on. False when a resume would meet the same failure. */
  canResume: boolean
  /** The Source the scan reads. */
  sourceId: string
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
  /** The ID of the Snapshot the scan made. Null until it completes. */
  snapshotId: string | null
}

/** `bind` asks "same as this element?": the element of another Source has the name of a fact of this one. */
export type ProposalAction = 'add' | 'change' | 'remove' | 'conflict' | 'relink' | 'bind'

/** What another Source of the project accepted for the same entity. Facts stay per Source; none wins by itself. */
export interface OtherSourceFact {
  sourceId: string
  /** The citation name, such as `github.com/acme/orders`. */
  source: string
  sourceName: string
  /** The truth label of the fact in that Source. */
  factStatus: string
  values: Record<string, string | null>
  observed: Record<string, string>
  /** `<source>@<revision>:path:start-end`, or null. */
  citation: string | null
  acceptedBy: string
  acceptedAt: string
}

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
  /** What other Sources accepted for the same entity. A conflict shows them side by side. */
  otherSources: OtherSourceFact[]
  sourceId: string
}

/** A fact of a scan that is no proposal: the model has no home for it, or it needs something the source does not state. */
export interface EvidenceItem {
  factKey: string
  kind: string
  name: string
  attributes: Record<string, string>
  evidence: ProposalEvidence[]
  /** Why the fact is no proposal. */
  reason: string
}

const FACT_REASONS: Record<string, string> = {
  'no-model-home': 'The model has no place for this kind of fact.',
  'several-code-declarations': 'The source declares it in more than one place, so Vellum cannot tell which one it is.',
  'no-owning-service': 'The source names no service that owns it.',
  'unsupported-transport': 'The model does not support its transport.',
  'unsupported-channel-kind': 'The model does not support its kind of channel.',
  'binding-channel-not-resolved': 'It names a channel that this scan did not find.',
  'no-channel-or-consumer': 'The source does not name its channel or its consumer.',
  'pattern-has-no-model-home': 'A pattern is evidence for a promise check, not a part of the model.',
  'no-producing-service': 'The source names no one service that produces it.',
  'contract-document-has-no-model-home': 'A contract document as a whole has no place in the model.',
}

/** Why a fact is no proposal, in words. An unknown code is shown as it is. */
export function describeFactReason(reason: string): string {
  return FACT_REASONS[reason] ?? reason
}

export interface DecisionResult {
  accepted: string[]
  rejected: string[]
  failed: { id: string; reason: string }[]
  /** The "same as X?" questions answered. */
  answered: string[]
  /** True when answers were given and the scan's pending proposals were planned again. */
  replanned: boolean
}

/** One answer to "same as X?". `same: false` makes the fact a new element. */
export interface BindAnswer {
  id: string
  same: boolean
}

export type SourceKind = 'github' | 'gitlab' | 'bitbucket' | 'azure-devops' | 'directory' | 'archive' | 'agent-push' | 'model-file' | 'traces' | 'newrelic'

/** One Snapshot: the manifest ID, and the commit SHA of a git Source. */
export interface SourceSnapshot {
  scanId: string
  snapshotId: string
  commitSha: string | null
  /** What a citation names after `@`: the commit SHA of a git Source, else the Snapshot ID. */
  revision: string
  ref: string | null
  files: number
  facts: number
  isComplete: boolean
  createdAt: string
}

/** The pending proposals of a Source: those of its newest completed tracking scan. */
export interface SourceReview {
  scanId: string | null
  pending: number
  additions: number
  changes: number
  removals: number
  conflicts: number
  crossSourceConflicts: number
  bindQuestions: number
  relinks: number
  /** What "accept all from this Source" would accept. */
  acceptable: number
}

export interface SourceSummary {
  id: string
  kind: SourceKind
  name: string
  host: string
  locator: string
  citation: string
  createdBy: string
  createdAt: string
  latestScan: ScanSummary | null
  latestSnapshot: SourceSnapshot | null
  review: SourceReview
}

export interface SourceBinding {
  elementId: string
  elementName: string
  elementKind: string
  externalKey: string
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

export type ImpactGatePolicy = 'off' | 'fail-on-broken'

/** The verdict of the gate policy of the project on one report. `off`: the policy is off and nothing fails. */
export interface ImpactGate {
  policy: ImpactGatePolicy
  verdict: 'pass' | 'fail' | 'off'
  reasons: string[]
}

export interface ImpactGateSetting {
  policy: ImpactGatePolicy
  changedBy: string | null
  changedAt: string | null
  /** True for a person with the owner role. */
  canChange: boolean
}

/** The compact report for pull request CI. Only the members the app reads are typed. */
export interface ImpactReport {
  format: number
  scanId: string
  gate: ImpactGate
  omitted: { changes: number; flows: number; promises: number }
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
  decide: (projectId: string, id: string, decision: { accept: string[]; reject: string[]; acknowledgeImpact: boolean; relink: { id: string; factKey: string | null }[]; bind?: BindAnswer[] }) =>
    api<DecisionResult>('POST', `${base(projectId)}/${id}/proposals/decide`, decision),
  /** One page of the facts of a scan that are no proposal, in the order the scan found them. */
  evidence: (projectId: string, id: string, cursor: string | null) =>
    api<Page<EvidenceItem>>('GET', `${base(projectId)}/${id}/evidence?limit=50${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`),
  impact: (projectId: string, id: string) => api<ChangeImpact>('GET', `${base(projectId)}/${id}/impact`),
  report: (projectId: string, id: string) => api<ImpactReport>('GET', `${base(projectId)}/${id}/impact/report`, undefined, { accept: 'application/json' }),
  /** The same report as Markdown, as a CI job reads it. */
  reportMarkdown: async (projectId: string, id: string): Promise<string> => {
    const response = await fetch(`${base(projectId)}/${id}/impact/report`, { headers: { Accept: 'text/markdown' } })
    if (!response.ok) {
      const payload = (await response.json().catch(() => ({}))) as { title?: string; detail?: string | null }
      throw new ApiError(response.status, [payload.title, payload.detail].filter(Boolean).join('. ') || 'The request did not complete.')
    }
    return response.text()
  },
  /** Runs the source pattern checks of the approved promises against the snapshot of this scan. Each result is for this scan only. */
  runChecks: (projectId: string, id: string) =>
    api<{ scanId: string; runs: { requirementId: string; key: string; result: string; detail: string | null }[]; skipped: number }>('POST', `${base(projectId)}/${id}/impact/checks`),
  gate: (projectId: string) => api<ImpactGateSetting>('GET', `/api/projects/${projectId}/impact-gate`),
  setGate: (projectId: string, policy: ImpactGatePolicy) => api<ImpactGateSetting>('PUT', `/api/projects/${projectId}/impact-gate`, { policy }),
  track: (projectId: string, id: string) => api<ScanDetail>('POST', `${base(projectId)}/${id}/track`),
  source: (projectId: string, id: string, evidence: Pick<ProposalEvidence, 'path' | 'startLine' | 'endLine'>) =>
    api<SourceWindow>(
      'GET',
      `${base(projectId)}/${id}/source?path=${encodeURIComponent(evidence.path)}&startLine=${evidence.startLine}&endLine=${evidence.endLine}`,
    ),
}

/** The kinds of Source a person can add on this instance. */
export type AddableKind = 'github' | 'gitlab' | 'bitbucket' | 'azure-devops' | 'directory' | 'archive'
export type GitHostKind = 'github' | 'gitlab' | 'bitbucket' | 'azure-devops'

export interface SourceKindInfo {
  kind: AddableKind
  available: boolean
  /** Why the kind cannot be used now. Null when it can. */
  reason: string | null
  /** The web host of a git host, or the sources root of a directory. */
  where: string | null
}

export interface SourceKinds {
  kinds: SourceKindInfo[]
  /** The workspace limit `import.maxArchiveBytes`: the most bytes the files of one archive may expand to. */
  maxArchiveBytes: number
}

export interface RepositoryChoice {
  /** The repository as a Source of the kind keeps it, such as `acme/orders`. */
  repository: string
  name: string
  defaultBranch: string
  isPrivate: boolean
  /** The Source this project already has for the repository. */
  sourceId: string | null
}

export interface RepositoryList {
  kind: GitHostKind
  owner: string
  page: number
  repositories: RepositoryChoice[]
  hasMore: boolean
}

export interface BulkAddItem {
  repository: string
  sourceId: string | null
  /** False when the project had the Source already. */
  created: boolean
  /** Null when no scan was queued: see `error`. */
  scanId: string | null
  error: { type: string; title: string; detail?: string | null } | null
}

/** What each kind is called, and how it names one place. */
export const KIND_INFO: Record<AddableKind, { label: string; locator: string; owner: string; placeholder: string }> = {
  github: { label: 'GitHub', locator: 'owner/name', owner: 'Organization or user', placeholder: 'acme' },
  gitlab: { label: 'GitLab', locator: 'group/name or group/subgroup/name', owner: 'Group', placeholder: 'acme or acme/platform' },
  bitbucket: { label: 'Bitbucket', locator: 'workspace/repository', owner: 'Workspace', placeholder: 'acme' },
  'azure-devops': { label: 'Azure DevOps', locator: 'organization/project/repository', owner: 'Organization or organization/project', placeholder: 'acme or acme/shop' },
  directory: { label: 'Folder', locator: 'a folder under the sources root', owner: '', placeholder: 'acme/orders' },
  archive: { label: 'Archive', locator: 'a .zip or .tar.gz file', owner: '', placeholder: '' },
}

export function isGitHost(kind: string): kind is GitHostKind {
  return kind === 'github' || kind === 'gitlab' || kind === 'bitbucket' || kind === 'azure-devops'
}

/**
 * What the revision of a Snapshot is called: a commit of a git Source, else the Snapshot ID.
 * The commit a directory's `.git/HEAD` names is metadata, not the revision.
 */
export function revisionLabel(kind: string): 'commit' | 'Snapshot' {
  return isGitHost(kind) ? 'commit' : 'Snapshot'
}

/** A byte count in the largest unit that keeps it at or above 1. */
export function formatBytes(bytes: number): string {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return unit === 0 ? `${bytes} bytes` : `${Number.isInteger(value) ? value : value.toFixed(1)} ${units[unit]}`
}

export const ARCHIVE_ACCEPT = '.zip,.tar.gz,.tgz,application/zip,application/gzip'

/** What one upload may hold beyond the expanded limit: headers and the central directory. The server answers 413 above it. */
const UPLOAD_OVERHEAD = 64 * 1024 * 1024

/** The largest upload the server takes for a workspace limit `import.maxArchiveBytes`. */
export function uploadCap(maxArchiveBytes: number): number {
  return maxArchiveBytes + UPLOAD_OVERHEAD
}

const sourcesBase = (projectId: string) => `/api/projects/${projectId}/sources`

/**
 * Sends a `.zip` or `.tar.gz` as the raw body. XMLHttpRequest, because fetch reports no upload
 * progress. Resolves with the ID of the scan the upload queued.
 */
function uploadArchive(projectId: string, sourceId: string, file: File, onProgress: (sent: number, total: number) => void): Promise<{ id: string }> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open('POST', `${sourcesBase(projectId)}/${sourceId}/archive`)
    request.withCredentials = true
    request.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
    request.upload.onprogress = (e) => onProgress(e.loaded, e.lengthComputable ? e.total : file.size)
    request.onload = () => {
      const data = request.responseText ? safeJson(request.responseText) : null
      if (request.status >= 200 && request.status < 300) resolve(data as { id: string })
      else if (request.status === 413 && !(data as { title?: string } | null)?.title) {
        reject(new ApiError(413, 'The file is larger than this workspace accepts for one upload.'))
      } else reject(apiError(request.status, data))
    }
    request.onerror = () => reject(new ApiError(0, 'The upload did not reach the server. Check the connection and try again.'))
    request.onabort = () => reject(new ApiError(0, 'The upload was cancelled.'))
    request.send(file)
  })
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

export const sourcesApi = {
  list: (projectId: string) => api<SourceSummary[]>('GET', sourcesBase(projectId)),
  kinds: (projectId: string) => api<SourceKinds>('GET', `${sourcesBase(projectId)}/kinds`),
  create: (projectId: string, body: { kind: AddableKind; repository?: string; path?: string; name?: string }) =>
    api<SourceSummary>('POST', sourcesBase(projectId), body),
  rename: (projectId: string, id: string, name: string) => api<SourceSummary>('PATCH', `${sourcesBase(projectId)}/${id}`, { name }),
  /** Deletes the Source, its Snapshots and the stored files no other Snapshot needs. The model keeps accepted facts. */
  remove: (projectId: string, id: string) => api<void>('DELETE', `${sourcesBase(projectId)}/${id}`),
  scan: (projectId: string, id: string, body: { ref?: string; pullRequest?: number; includePaths?: string[] }) =>
    api<{ id: string }>('POST', `${sourcesBase(projectId)}/${id}/scans`, body),
  uploadArchive,
  repositories: (projectId: string, kind: GitHostKind, owner: string, page: number) =>
    api<RepositoryList>('GET', `${sourcesBase(projectId)}/repositories?kind=${kind}&owner=${encodeURIComponent(owner)}&page=${page}`),
  bulk: (projectId: string, body: { kind: GitHostKind; repositories: string[]; ref?: string }) =>
    api<{ items: BulkAddItem[] }>('POST', `${sourcesBase(projectId)}/bulk`, body),
  snapshots: (projectId: string, id: string) => api<SourceSnapshot[]>('GET', `${sourcesBase(projectId)}/${id}/snapshots`),
  bindings: (projectId: string, id: string) => api<SourceBinding[]>('GET', `${sourcesBase(projectId)}/${id}/bindings`),
  /** Accepts every pending addition, change and removal of the Source. Conflicts and questions stay for a person. */
  acceptAll: (projectId: string, id: string, acknowledgeImpact: boolean) =>
    api<DecisionResult>('POST', `${sourcesBase(projectId)}/${id}/accept`, { acknowledgeImpact }),
}

/** One line that says what a Source waits for. */
export function describeReview(source: SourceSummary): string {
  const r = source.review
  if (!source.latestSnapshot) return source.latestScan ? 'No Snapshot yet' : 'Not scanned yet'
  if (r.pending === 0) return 'Nothing waits for a decision'
  return [
    `${r.pending} pending`,
    r.bindQuestions > 0 && `${r.bindQuestions} “same as?”`,
    r.crossSourceConflicts > 0 && `${r.crossSourceConflicts} ${r.crossSourceConflicts === 1 ? 'conflict' : 'conflicts'} with other Sources`,
  ].filter(Boolean).join(' · ')
}

/** The fields where this Source and the others say different things, for a side-by-side view. */
export function sourceSides(proposal: Pick<Proposal, 'proposed' | 'current' | 'otherSources' | 'changes'>): {
  fields: string[]
  model: Record<string, unknown>
  here: Record<string, unknown>
} {
  const here = (proposal.proposed ?? {}) as Record<string, unknown>
  const model = (proposal.current ?? {}) as Record<string, unknown>
  const compared = new Set<string>()
  for (const change of proposal.changes) if (!change.field.startsWith('observed.') && change.field !== 'factStatus') compared.add(change.field)
  for (const other of proposal.otherSources) {
    for (const [field, value] of Object.entries(other.values)) {
      if (field in here && JSON.stringify(here[field] ?? null) !== JSON.stringify(value ?? null)) compared.add(field)
    }
  }
  if (compared.size === 0) for (const field of ['name', 'technology']) if (field in here) compared.add(field)
  return { fields: [...compared].sort(), model, here }
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
      return scan.retryAt ? `Paused by the rate limit of the host until ${new Date(scan.retryAt).toLocaleTimeString()}` : 'Paused by the rate limit of the host'
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
