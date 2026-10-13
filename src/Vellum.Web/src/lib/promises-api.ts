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

/** Where the result of a test plan must come from. Vellum reads the run from GitHub and compares it with these values. */
export interface EvidenceSource {
  system: 'github-actions'
  repository: string
  workflow: string
  job: string | null
}

export interface ObligationRecipe {
  text: string
  scenarioCheck?: unknown
  sourcePattern?: unknown
  plan?: {
    steps: string[]
    inputs: string[]
    expectedObservations: string[]
    forbiddenObservations: string[]
    contractIds: string[]
    evidenceSource: EvidenceSource | null
  } | null
}

export interface Obligation {
  id: string
  key: string
  kind: string
  supported: boolean
  execution: 'deterministic' | 'external-evidence' | 'none'
  title: string
  recipe: ObligationRecipe
  source: 'generated' | 'manual'
}

export interface AssertionRecord {
  id: string
  passed: boolean
  detail: string
  vacuous?: boolean
  vacuousReason?: string | null
}

export interface ObligationStatus {
  obligationId: string
  result: ObligationResult
  /** False when something the result read has changed since. Null when there is no result. */
  current: boolean | null
  /** One sentence for each input that changed. */
  changed: string[]
  assertions: AssertionRecord[] | null
  at: string | null
  environment: string | null
  commitSha: string | null
  scope: string | null
  evidence: Evidence[]
  limitations: string[]
  detail: string
}

export interface PromiseCounts {
  /** Results that are current. A stale result is counted in `stale` only. */
  passed: number
  failed: number
  stale: number
  notRun: number
  unsupported: number
}

export interface PromiseRevision {
  revision: number
  /** The hash of everything an approver reads. Approval names it. */
  contentHash: string
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
  hasGaps: boolean
  counts: PromiseCounts
}

export interface PromiseDetail extends Omit<PromiseSummary, 'counts' | 'hasDraft' | 'hasGaps' | 'title'> {
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

export interface VerifyResult {
  /** True only when every file, the manifest and the archive bytes are what Vellum writes for the pinned state. */
  identical: boolean
  /** True when the files and the manifest are what the pinned versions generate. */
  matchesPinned: boolean
  manifest: 'match' | 'differs' | string
  manifestDifferences: string[]
  archive: string | null
  /** True when the pinned versions are still the latest. */
  current: boolean
  moved: { kind: string; id: string; pinned: string | null; current: string | null }[]
  files: { path: string; status: string; expectedSha256: string | null; actualSha256: string | null }[]
}

export interface BuildManifest {
  /** `decisions` names the pinned decision streams. A manifest of the format before the review has none. */
  pinned: { projectId: string; projectName: string; branch: string; streamVersion: number; revisionId: string | null; decisions?: { id: string; version: number }[] | null }
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
  approve: (projectId: string, id: string, revision: number, contentHash: string) =>
    api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/approve`, { revision, contentHash }),
  updateObligation: (projectId: string, id: string, obligation: Obligation, recipe: ObligationRecipe) =>
    api<PromiseDetail>('PUT', `${base(projectId)}/promises/${id}/obligations/${obligation.key}`, { kind: obligation.kind, title: obligation.title, recipe }),
  recordEvidence: (projectId: string, id: string, key: string, runUrl: string, commitSha: string | null, revision: number | null) =>
    api<unknown>('POST', `${base(projectId)}/promises/${id}/obligations/${key}/evidence`, {
      result: 'passed', source: { system: 'github-actions', runUrl, commitSha, conclusion: null }, artifactDigest: null, revision,
    }),
  remove: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/promises/${id}`),
  generate: (projectId: string, id: string) =>
    api<{ promise: PromiseDetail; added: string[]; skipped: { key: string; reason: string }[] }>('POST', `${base(projectId)}/promises/${id}/obligations/generate`),
  /** `revision` null runs the approved revision in force. A draft is run only by its number, and that run never counts after approval. */
  run: (projectId: string, id: string, key: string, scanId: string | null, revision: number | null) =>
    api<unknown>('POST', `${base(projectId)}/promises/${id}/obligations/${key}/run`, { modelRevisionId: null, branchId: null, scanId, revision }),
  manifest: (projectId: string) => api<BuildManifest>('GET', `${base(projectId)}/build/manifest`),
}

/** Asks the server whether a bundle file is what Vellum generates for the versions the bundle names. */
export async function verifyBundle(projectId: string, file: File): Promise<VerifyResult> {
  const response = await fetch(`${base(projectId)}/build/verify`, { method: 'POST', headers: { 'Content-Type': 'application/zip' }, body: file })
  if (response.ok) return (await response.json()) as VerifyResult
  const problem = (await response.json().catch(() => null)) as { type?: string; detail?: string; errors?: { field: string; message: string }[] } | null
  if (problem?.type === 'invalid_bundle') throw new Error(`This file is not a bundle that Vellum wrote. ${problem.errors?.[0]?.message ?? problem.detail ?? ''}`.trim())
  if (problem?.type === 'pinned_input_unavailable') throw new Error(problem.detail ?? 'A version that the bundle names is no longer stored.')
  throw new Error(response.status === 413 ? 'The file is larger than the limit.' : (problem?.detail ?? 'The check did not complete.'))
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
  const total = counts.passed + counts.failed + counts.stale + counts.notRun + counts.unsupported
  if (total === 0) return 'No obligations'
  const parts = [
    counts.failed > 0 && `${counts.failed} failed`,
    counts.passed > 0 && `${counts.passed} passed`,
    counts.stale > 0 && `${counts.stale} stale`,
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
