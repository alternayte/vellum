import { api } from '@/lib/settings-api'

export interface PromiseLinks {
  scenarioIds: string[]
  operationIds: string[]
  channelIds: string[]
  subscriptionIds: string[]
  messageIds: string[]
  elementIds: string[]
}

export type ObligationResult = 'passed' | 'failed' | 'not-run' | 'unsupported'

export interface Evidence {
  kind: 'trace' | 'source' | 'external'
  hash: string | null
  scenarioId: string | null
  pointer: string | null
  url: string | null
  identity: 'verified' | 'claimed' | null
  identityReason: string | null
  claimedResult: string | null
}

export interface Obligation {
  id: string
  key: string
  kind: string
  supported: boolean
  execution: 'deterministic' | 'external-evidence' | 'none'
  title: string
  recipe: { text: string }
  source: 'generated' | 'manual'
}

export interface ObligationStatus {
  obligationId: string
  result: ObligationResult
  at: string | null
  environment: string | null
  commitSha: string | null
  scope: string | null
  evidence: Evidence[]
  limitations: string[]
  detail: string
}

export interface PromiseCounts {
  passed: number
  failed: number
  notRun: number
  unsupported: number
}

export interface PromiseRevision {
  revision: number
  status: 'draft' | 'approved'
  retires: boolean
  inForce: boolean
  title: string
  claim: string
  scope: string | null
  preconditions: string | null
  expectedOutcome: string | null
  forbiddenOutcome: string | null
  evidenceCriteria: string | null
  forbiddenEvents: { kind: string; target: string | null }[]
  requiresPattern: { repository: string; pattern: string; service: string | null } | null
  links: PromiseLinks
  obligations: Obligation[]
  approvedBy: string | null
  approvedAt: string | null
  evidenceStatus: { counts: PromiseCounts; obligations: ObligationStatus[]; gaps: string[] }
}

export interface PromiseSummary {
  id: string
  requirementId: string
  title: string
  state: 'draft' | 'approved' | 'retired'
  origin: string
  hasDraft: boolean
  needsReview: boolean
  counts: PromiseCounts
}

export interface PromiseDetail extends Omit<PromiseSummary, 'counts' | 'hasDraft' | 'title'> {
  latestRevision: number
  inForceRevision: number | null
  missingLinks: { kind: string; id: string }[]
  current: PromiseRevision
  draft: PromiseRevision | null
}

export interface PromiseInput {
  title: string
  claim: string
  scope: string | null
  preconditions: string | null
  expectedOutcome: string | null
  forbiddenOutcome: string | null
  evidenceCriteria: string | null
  forbiddenEvents: { kind: string; target: string | null }[]
  requiresPattern: { repository: string; pattern: string; service: string | null } | null
  links: PromiseLinks
}

export interface BuildManifest {
  pinned: { projectId: string; branch: string; streamVersion: number; revisionId: string | null }
  files: { path: string; sha256: string; bytes: number }[]
  bundleHash: string
  buildReady: boolean
  blockers: { code: string; subject: string; message: string }[]
  selfCheck: { passed: boolean; notes: string[] }
}

const base = (projectId: string) => `/api/projects/${projectId}`

export const promisesApi = {
  list: async (projectId: string) => (await api<{ items: PromiseSummary[] }>('GET', `${base(projectId)}/promises?limit=200`)).items,
  get: (projectId: string, id: string) => api<PromiseDetail>('GET', `${base(projectId)}/promises/${id}`),
  prefix: (projectId: string) => api<{ prefix: string | null; nextNumber: number }>('GET', `${base(projectId)}/promises/prefix`),
  setPrefix: (projectId: string, prefix: string) => api<{ prefix: string }>('PUT', `${base(projectId)}/promises/prefix`, { prefix, startAt: null }),
  create: (projectId: string, input: PromiseInput) => api<PromiseDetail>('POST', `${base(projectId)}/promises`, { ...input, origin: 'human' }),
  update: (projectId: string, id: string, input: PromiseInput) => api<PromiseDetail>('PUT', `${base(projectId)}/promises/${id}`, input),
  revise: (projectId: string, id: string) => api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/revise`, { retire: false }),
  approve: (projectId: string, id: string, revision: number) => api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/approve`, { revision }),
  remove: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/promises/${id}`),
  generate: (projectId: string, id: string) =>
    api<{ promise: PromiseDetail; added: string[]; skipped: { key: string; reason: string }[] }>('POST', `${base(projectId)}/promises/${id}/obligations/generate`),
  run: (projectId: string, id: string, key: string, scanId: string | null) =>
    api<unknown>('POST', `${base(projectId)}/promises/${id}/obligations/${key}/run`, { modelRevisionId: null, branchId: null, scanId, revision: null }),
  manifest: (projectId: string) => api<BuildManifest>('GET', `${base(projectId)}/build/manifest`),
}

/** Downloads the SDD or the bundle. The server names the file. */
export async function downloadBuild(projectId: string, format: 'sdd' | 'bundle'): Promise<void> {
  const response = await fetch(`${base(projectId)}/build/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ revisionId: null, branchId: null, format }),
  })
  if (!response.ok) throw new Error(response.status === 413 ? 'The export is larger than the limit.' : 'The export did not complete.')
  const name = /filename="?([^";]+)"?/.exec(response.headers.get('Content-Disposition') ?? '')?.[1] ?? (format === 'sdd' ? 'SDD.md' : 'vellum-bundle.zip')
  const url = URL.createObjectURL(await response.blob())
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  URL.revokeObjectURL(url)
}

export const RESULT_LABEL: Record<ObligationResult, string> = {
  passed: 'Passed',
  failed: 'Failed',
  'not-run': 'Not run',
  unsupported: 'Unsupported',
}

export const RESULT_COLOR: Record<ObligationResult, string> = {
  passed: 'var(--trace-request)',
  failed: 'var(--trace-failure)',
  'not-run': 'var(--muted-foreground)',
  unsupported: 'var(--trace-retry)',
}

/**
 * The state of a promise in one phrase. There is no single "verified": the phrase counts
 * what passed, in what scope, and what has no evidence yet.
 */
export function describeCounts(counts: PromiseCounts): string {
  const total = counts.passed + counts.failed + counts.notRun + counts.unsupported
  if (total === 0) return 'No obligations'
  const parts = [
    counts.failed > 0 && `${counts.failed} failed`,
    counts.passed > 0 && `${counts.passed} passed`,
    counts.notRun > 0 && `${counts.notRun} not run`,
    counts.unsupported > 0 && `${counts.unsupported} unsupported`,
  ].filter(Boolean)
  return parts.join(' · ')
}

export function emptyPromise(): PromiseInput {
  return {
    title: '', claim: '', scope: null, preconditions: null, expectedOutcome: null, forbiddenOutcome: null, evidenceCriteria: null,
    forbiddenEvents: [], requiresPattern: null,
    links: { scenarioIds: [], operationIds: [], channelIds: [], subscriptionIds: [], messageIds: [], elementIds: [] },
  }
}
