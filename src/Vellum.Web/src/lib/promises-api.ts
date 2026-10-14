import { api } from '@/lib/settings-api'
import type { TraceEventKind } from '@/lib/trace'

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

export const FAULT_KINDS = ['Outage', 'Error', 'Timeout'] as const
export const STEP_OUTCOMES = ['Succeeded', 'Failed', 'Skipped'] as const
export const SOURCE_PATTERNS = ['transactional-outbox', 'in-memory-outbox'] as const

/** Makes attempts against a target fail. `failFirst` null fails every attempt. */
export interface Fault {
  target: string
  kind: (typeof FAULT_KINDS)[number]
  failFirst: number | null
}

export type Assertion =
  | { type: 'outcome'; id: string; step: string; expected: (typeof STEP_OUTCOMES)[number] }
  | { type: 'event-count'; id: string; kind: TraceEventKind; target: string | null; expected: number }

export interface ScenarioCheckRecipe {
  scenarioId: string
  faults: Fault[]
  expect: Assertion[]
}

export interface SourcePatternRecipe {
  repository: string
  pattern: string
  service: string | null
  /** Null and true: the pattern must be declared with no compile-time condition. */
  requireUnconditional?: boolean | null
}

export interface PlanRecipe {
  steps: string[]
  inputs: string[]
  expectedObservations: string[]
  forbiddenObservations: string[]
  contractIds: string[]
  evidenceSource: EvidenceSource | null
}

export interface ObligationRecipe {
  text: string
  scenarioCheck?: ScenarioCheckRecipe | null
  sourcePattern?: SourcePatternRecipe | null
  plan?: PlanRecipe | null
}

export interface ObligationInput {
  kind: string
  title: string
  recipe: ObligationRecipe
}

export const OBLIGATION_KINDS: { kind: string; label: string; help: string }[] = [
  { kind: 'scenario-check', label: 'Scenario check', help: 'Vellum plays a scenario with the faults you add and evaluates the assertions.' },
  { kind: 'source-pattern-check', label: 'Source pattern check', help: 'Vellum looks at one scan of a repository for a pattern that the source declares.' },
  { kind: 'contract-test', label: 'Contract test plan', help: 'A test plan. Vellum cannot run it. Its result comes from your CI.' },
  { kind: 'integration-test', label: 'Integration test plan', help: 'A test plan. Vellum cannot run it. Its result comes from your CI.' },
  { kind: 'failure-test', label: 'Failure test plan', help: 'A test plan. Vellum cannot run it. Its result comes from your CI.' },
]

export interface Obligation {
  id: string
  key: string
  kind: string
  supported: boolean
  execution: 'deterministic' | 'external-evidence' | 'none'
  title: string
  recipe: ObligationRecipe
  /** Who wrote the obligation first: the generator, a person, or an agent token. */
  source: 'generated' | 'manual' | 'agent'
  /** Who added it to this draft: `user:<id>` or `agent-token:<id>`. Absent for an obligation of an approved revision. */
  addedBy?: string | null
  lastChangedBy?: string | null
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
  /** For a scenario check: the faults that were played, the scenario's and the obligation's. */
  effectiveFaults: Fault[] | null
  at: string | null
  environment: string | null
  /** The model the check ran on. */
  modelRevision: { streamId: string; version: number; revisionId: string | null; revisionName: string | null } | null
  commitSha: string | null
  scanId: string | null
  inputs: { ref?: string | null; scanKind?: string | null } | null
  scope: string | null
  evidence: Evidence[]
  limitations: string[]
  detail: string
}

/** One stored run of an obligation. Runs are appended and never changed. */
export interface VerificationRun {
  id: string
  obligationId: string
  promiseRevision: number
  result: ObligationResult
  at: string
  environment: string
  modelRevision: ObligationStatus['modelRevision']
  commitSha: string | null
  scanId: string | null
  inputs: { ref?: string | null; scanKind?: string | null } | null
  scope: string
  detail: string
  /** True when the revision was a draft at the run. Such a run does not count after approval. */
  draft?: boolean | null
  /** The user of the call. For an agent token, the user who created the token. */
  startedBy: string
  /** `user:<id>` or `agent-token:<id>`. Null in a run recorded before the actor was kept. */
  actor?: string | null
}

/** Who or what started runs: a person, an agent token, or a CI token (scope `scan-pull-request`). Name null: the account or token is gone. */
export interface RunStarter {
  kind: 'person' | 'agent-token' | 'ci-token'
  name: string | null
}

export interface RunsPage {
  items: VerificationRun[]
  cursor: string | null
  /** By actor. A run without an actor is under `user:<startedBy>`. */
  startedBy: Record<string, RunStarter>
}

/** The key of a run in `RunsPage.startedBy`. */
export function runActor(run: VerificationRun): string {
  return run.actor ?? `user:${run.startedBy}`
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
  /** Who changed the revision last: `user:<id>` for a person, `agent-token:<id>` for an agent token. Absent when it was not recorded. */
  lastChangedBy?: string | null
  lastChangedAt?: string | null
  /** For a revision that retires: the revision it retires. */
  retiresRevision?: number | null
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
  /** Who changed the draft last, when the promise has a draft. */
  draftLastChangedBy?: string | null
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
  /** True when the pinned state has a seal that this instance issued for this project. Only such a state is exported again. */
  issued: boolean
  moved: { kind: string; id: string; pinned: string | null; current: string | null }[]
  files: { path: string; status: string; expectedSha256: string | null; actualSha256: string | null }[]
}

export interface BuildManifest {
  /**
   * `decisions` names the pinned decision streams. A manifest of the format before the review has none.
   * `seal` says that Vellum issued the state. An export takes a pinned state only whole, with its seal.
   */
  pinned: { projectId: string; projectName: string; branch: string; streamVersion: number; revisionId: string | null; decisions?: { id: string; version: number }[] | null; seal?: string | null }
  files: { path: string; sha256: string; bytes: number }[]
  bundleHash: string
  buildReady: boolean
  blockers: { code: string; subject: string; message: string }[]
  selfCheck: { passed: boolean; notes: string[] }
}

/** What a brief is for: promises, the change of one scan, or elements. */
export interface BriefScope {
  promiseIds?: string[]
  changeScanId?: string | null
  elementIds?: string[]
}

export interface BriefResult {
  /** The brief as Markdown. */
  brief: string
  manifest: {
    format: string
    pinned: BuildManifest['pinned']
    included: {
      elementIds: string[]; operationIds: string[]; channelIds: string[]; subscriptionIds: string[]
      messageIds: string[]; schemaIds: string[]; scenarioIds: string[]; requirementIds: string[]
    }
    /** Promises in scope that no person approved. They are not requirements. */
    notApproved: string[]
    retired: string[]
    /** Facts in scope that are inferred, unknown or stale. The brief leaves them out of its fact tables. */
    unconfirmedFacts: string[]
    sha256: string
    bytes: number
    buildReady: boolean
    blockers: BuildManifest['blockers']
    changeScanId?: string | null
  }
}

const base = (projectId: string) => `/api/projects/${projectId}`

export const promisesApi = {
  list: async (projectId: string) => (await api<{ items: PromiseSummary[] }>('GET', `${base(projectId)}/promises?limit=200`)).items,
  get: (projectId: string, id: string) => api<PromiseDetail>('GET', `${base(projectId)}/promises/${id}`),
  prefix: (projectId: string) => api<{ prefix: string | null; nextNumber: number }>('GET', `${base(projectId)}/promises/prefix`),
  /** `startAt` is the number of the first promise. Null starts at 1. */
  setPrefix: (projectId: string, prefix: string, startAt: number | null) =>
    api<{ prefix: string; nextNumber: number }>('PUT', `${base(projectId)}/promises/prefix`, { prefix, startAt }),
  create: (projectId: string, input: PromiseInput) => api<PromiseDetail>('POST', `${base(projectId)}/promises`, { ...input, origin: 'human' }),
  update: (projectId: string, id: string, input: PromiseInput) => api<PromiseDetail>('PUT', `${base(projectId)}/promises/${id}`, input),
  /** `retire` true starts a revision that retires the promise when an owner approves it. */
  revise: (projectId: string, id: string, retire = false) => api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/revise`, { retire }),
  approve: (projectId: string, id: string, revision: number, contentHash: string) =>
    api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/approve`, { revision, contentHash }),
  updateObligation: (projectId: string, id: string, obligation: Obligation, recipe: ObligationRecipe) =>
    api<PromiseDetail>('PUT', `${base(projectId)}/promises/${id}/obligations/${obligation.key}`, { kind: obligation.kind, title: obligation.title, recipe }),
  addObligation: (projectId: string, id: string, input: ObligationInput) =>
    api<PromiseDetail>('POST', `${base(projectId)}/promises/${id}/obligations`, input),
  changeObligation: (projectId: string, id: string, key: string, input: ObligationInput) =>
    api<PromiseDetail>('PUT', `${base(projectId)}/promises/${id}/obligations/${key}`, input),
  removeObligation: (projectId: string, id: string, key: string) =>
    api<PromiseDetail>('DELETE', `${base(projectId)}/promises/${id}/obligations/${key}`),
  recordEvidence: (projectId: string, id: string, key: string, runUrl: string, commitSha: string | null, revision: number | null) =>
    api<unknown>('POST', `${base(projectId)}/promises/${id}/obligations/${key}/evidence`, {
      result: 'passed', source: { system: 'github-actions', runUrl, commitSha, conclusion: null }, artifactDigest: null, revision,
    }),
  remove: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/promises/${id}`),
  /** The runs of one obligation, newest first, 20 at a time. */
  runs: (projectId: string, id: string, obligationId: string, cursor: string | null) =>
    api<RunsPage>('GET', `${base(projectId)}/promises/${id}/runs?obligation=${encodeURIComponent(obligationId)}&limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`),
  generate: (projectId: string, id: string) =>
    api<{ promise: PromiseDetail; added: string[]; skipped: { key: string; reason: string }[] }>('POST', `${base(projectId)}/promises/${id}/obligations/generate`),
  /** `revision` null runs the approved revision in force. A draft is run only by its number, and that run never counts after approval. */
  run: (projectId: string, id: string, key: string, scanId: string | null, revision: number | null) =>
    api<{ result: ObligationResult; detail: string }>('POST', `${base(projectId)}/promises/${id}/obligations/${key}/run`, { modelRevisionId: null, branchId: null, scanId, revision }),
  manifest: (projectId: string) => api<BuildManifest>('GET', `${base(projectId)}/build/manifest`),
  brief: (projectId: string, scope: BriefScope) =>
    api<BriefResult>('POST', `${base(projectId)}/build/brief`, {
      promiseIds: scope.promiseIds ?? [], scenarioIds: [], elementIds: scope.elementIds ?? [],
      revisionId: null, branchId: null, changeScanId: scope.changeScanId ?? null,
    }),
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

const AGENT_ACTOR = 'agent-token:'

/** True when an actor, as the server names it, is an agent token and not a person. */
export function isAgentActor(actor: string | null | undefined): actor is string {
  return typeof actor === 'string' && actor.startsWith(AGENT_ACTOR)
}

/** The start of a token ID, enough to find the token in the list of agent tokens. */
export function shortAgent(actor: string): string {
  return actor.slice(AGENT_ACTOR.length, AGENT_ACTOR.length + 8)
}

/**
 * The result of an obligation as words and one colour. A passed or failed result on inputs
 * that moved is stale: it is shown as stale and never as a pass.
 */
export function describeResult(status: Pick<ObligationStatus, 'result' | 'current'> | undefined, supported: boolean): { label: string; color: string } {
  const result = status?.result ?? (supported ? 'not-run' : 'unsupported')
  if (status?.current === false) return { label: `Stale (${RESULT_LABEL[result].toLowerCase()} on older inputs)`, color: 'var(--trace-retry)' }
  return { label: RESULT_LABEL[result], color: RESULT_COLOR[result] }
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
