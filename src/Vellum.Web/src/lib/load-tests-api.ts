import { ApiError } from '@/lib/settings-api'
import type { StressRunSummary } from '@/lib/stress-api'

/**
 * Typed access to the load-test endpoints: targets, generated k6 scripts and measured
 * results. Vellum writes the script and reads the result. It does not run k6.
 */

export type TargetEnvironment = 'development' | 'test' | 'staging' | 'production'
export const TARGET_ENVIRONMENTS: TargetEnvironment[] = ['development', 'test', 'staging', 'production']

export interface TargetAuthorisation { baseUrl: string; environment: TargetEnvironment; by: string; at: string }
export interface TargetOverride { allowNonIdempotent: boolean; allowFailureInjection: boolean; reason: string; by: string; at: string }

export interface LoadTestTarget {
  id: string
  name: string
  baseUrl: string
  environment: TargetEnvironment
  version: number
  /** Null when no owner authorised the base URL and the class the target has now. */
  authorisation: TargetAuthorisation | null
  /** Null when the production defaults apply. */
  override: TargetOverride | null
  updatedAt: string
}

export interface K6NotCovered { stepId: string; kind: string; reason: string }

export interface K6Plan {
  generator: string
  executor: string
  operations: { index: number; operationId: string; name: string; ownerId: string; ownerName: string }[]
  steps: { stepId: string; operation: number; method: string; path: string; hasBody: boolean; expect: 'success' | 'failure'; timeoutMs: number }[]
  notCovered: K6NotCovered[]
  notes: string[]
  warmUpMs: number
  durationMs: number
  faults: number
  /** The executor options in one line. The script stops when k6 runs other options. Null in a plan of generator 1. */
  load: string | null
  plannedIterations: number | null
  minElapsedMs: number | null
  loadNotes: string[] | null
}

export interface K6Script {
  id: string
  hash: string
  fileName: string
  generator: string
  scenarioId: string
  scenarioVersion: number
  modelStreamId: string
  modelVersion: number
  profileId: string
  profileVersion: number
  targetId: string
  targetName: string
  baseUrl: string
  environment: TargetEnvironment
  authorisedBy: string
  authorisedAt: string
  createdAt: string
  plan: K6Plan
  command: string
  ciWorkflow: string
  /** Null in a list. */
  content: string | null
}

export interface MeasuredLatency { label: 'measured'; count: number; minMs: number; meanMs: number; p50Ms: number; p90Ms: number; p95Ms: number; p99Ms: number; maxMs: number }

export interface MeasuredOperation {
  label: 'measured'
  operationId: string
  name: string
  requests: number
  /** Requests whose status was not the one the script expected. For a step that expects a failure, a 2xx status is unexpected. A 3xx status is always unexpected. */
  unexpectedStatus: number
  unexpectedStatusRate: number | null
  throughputPerSecond: number
  /** Requests that expected a 2xx status and got one. */
  latency: MeasuredLatency | null
  /** Requests that expected a failure and got one. They are in no success latency. */
  expectedFailures: number
  expectedFailureLatency: MeasuredLatency | null
}

export interface MeasuredResultSummary {
  id: string
  label: 'measured'
  scenarioId: string
  scenarioVersion: number
  modelStreamId: string
  modelVersion: number
  profileId: string
  profileVersion: number
  startedAt: string
  endedAt: string
  measuredSeconds: number
  provenance: {
    kind: string
    format: string
    scriptId: string
    scriptHash: string
    contentHash: string
    targetId: string
    targetName: string
    baseUrl: string
    environment: TargetEnvironment
    importedBy: string
    importedVia: string | null
    importedAt: string
  }
  flow: { label: 'measured'; iterations: number; unexpectedStatus: number; droppedIterations: number; unexpectedStatusRate: number | null; throughputPerSecond: number; latency: MeasuredLatency | null }
  stale: boolean
  staleBecause: ('model' | 'scenario')[]
}

export interface MeasuredResult {
  result: MeasuredResultSummary
  operations: MeasuredOperation[]
  notCovered: K6NotCovered[]
  /** The faults that the scenario declares. The script injects none. */
  faults: number
  /** True when a step of the script expects a failure. The flow then has no success latency. */
  expectsFailure: boolean
}

export type EvidenceLabel = 'estimated' | 'measured' | 'unknown'

export interface EstimateRow {
  scope: 'flow' | 'operation'
  target: string | null
  name: string
  metric: string
  unit: string
  estimated: number | null
  estimatedLabel: EvidenceLabel
  /** Null when the script did not measure it. Null is not zero. */
  measured: number | null
  measuredLabel: EvidenceLabel
  difference: number | null
  /** Why the measured side is unknown although the run has a number: the run measured another thing than the simulation estimates. */
  reason: string | null
}

export interface EstimateComparison { estimate: StressRunSummary; measured: MeasuredResultSummary; rows: EstimateRow[]; differences: string[]; note: string }

export type Verdict = 'regressed' | 'improved' | 'unchanged' | 'unknown'

export interface RegressionRow {
  scope: 'flow' | 'operation'
  target: string | null
  name: string
  metric: string
  unit: string
  baseline: number | null
  candidate: number | null
  change: number | null
  relative: number | null
  verdict: Verdict
}

export interface Regression {
  label: 'measured'
  baseline: MeasuredResultSummary
  candidate: MeasuredResultSummary
  rows: RegressionRow[]
  subjects: { scope: string; target: string | null; name: string; regressed: boolean; regressedMetrics: string[] }[]
  regressions: number
  differences: string[]
  rule: string
}

export interface FieldProblem { field: string; message: string }

/**
 * A request that the server understood and refused: no script was generated, or a result
 * file was not stored. `reasons` and `errors` say why; an error names a member of the file.
 */
export class LoadTestRefused extends Error {
  type: string
  detail: string | null
  reasons: string[]
  errors: FieldProblem[]

  constructor(type: string, title: string, detail: string | null, reasons: string[], errors: FieldProblem[]) {
    super(title)
    this.type = type
    this.detail = detail
    this.reasons = reasons
    this.errors = errors
  }
}

async function call<T>(method: string, url: string, body?: unknown, raw = false): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : raw ? (body as string) : JSON.stringify(body),
  })
  const text = await response.text()
  let data: unknown
  try {
    data = text ? JSON.parse(text) : null
  } catch {
    // A body that is not JSON has nothing to show; the status decides.
  }
  if (response.ok) return data as T

  const payload = (data ?? {}) as { type?: string; title?: string; detail?: string | null; reasons?: string[]; errors?: FieldProblem[] | null }
  if (response.status === 422) {
    throw new LoadTestRefused(
      payload.type ?? 'refused', payload.title ?? 'The request was refused.', payload.detail ?? null,
      Array.isArray(payload.reasons) ? payload.reasons : [], Array.isArray(payload.errors) ? payload.errors : [])
  }
  throw new ApiError(
    response.status,
    payload.title
      ? (payload.detail ? `${payload.title}. ${payload.detail}` : payload.title)
      : response.status === 403 ? 'You do not have permission to do this.'
        : response.status === 413 ? 'The file is too large. A result file has at most 256 KB.'
          : 'The request did not complete.',
    Array.isArray(payload.errors) ? payload.errors : [],
  )
}

const base = (projectId: string) => `/api/projects/${projectId}/load-tests`

export const loadTestsApi = {
  targets: (projectId: string) => call<LoadTestTarget[]>('GET', `${base(projectId)}/targets`),
  addTarget: (projectId: string, target: { name: string; baseUrl: string; environment: string }) =>
    call<LoadTestTarget>('POST', `${base(projectId)}/targets`, target),
  changeTarget: (projectId: string, id: string, target: { name: string; baseUrl: string; environment: string }) =>
    call<LoadTestTarget>('PUT', `${base(projectId)}/targets/${id}`, target),
  removeTarget: (projectId: string, id: string) => call<void>('DELETE', `${base(projectId)}/targets/${id}`),
  /** The caller names what it read: the base URL, the class and the version. A target that differs in one of them is not authorised (409). */
  authorise: (projectId: string, target: Pick<LoadTestTarget, 'id' | 'baseUrl' | 'environment' | 'version'>) =>
    call<LoadTestTarget>('POST', `${base(projectId)}/targets/${target.id}/authorise`, { baseUrl: target.baseUrl, environment: target.environment, version: target.version }),
  revoke: (projectId: string, id: string) => call<LoadTestTarget>('DELETE', `${base(projectId)}/targets/${id}/authorisation`),
  setOverride: (projectId: string, id: string, body: { allowNonIdempotent: boolean; allowFailureInjection: boolean; reason: string; confirmName: string }) =>
    call<LoadTestTarget>('PUT', `${base(projectId)}/targets/${id}/override`, body),

  generate: (projectId: string, targetId: string, profileId: string, branchId?: string | null) =>
    call<K6Script>('POST', `${base(projectId)}/scripts${branchId ? `?branchId=${branchId}` : ''}`, { targetId, profileId }),
  script: (projectId: string, id: string) => call<K6Script>('GET', `${base(projectId)}/scripts/${id}`),

  /** `file` is the text of the result file, sent as it is. */
  importResult: (projectId: string, file: string) => call<MeasuredResult>('POST', `${base(projectId)}/results`, file, true),
  results: (projectId: string) => call<MeasuredResultSummary[]>('GET', `${base(projectId)}/results`),
  result: (projectId: string, id: string) => call<MeasuredResult>('GET', `${base(projectId)}/results/${id}`),
  removeResult: (projectId: string, id: string) => call<void>('DELETE', `${base(projectId)}/results/${id}`),

  compareEstimate: (projectId: string, resultId: string, runId: string) =>
    call<EstimateComparison>('GET', `${base(projectId)}/compare/estimate?resultId=${resultId}&runId=${runId}`),
  compareResults: (projectId: string, baseline: string, candidate: string) =>
    call<Regression>('GET', `${base(projectId)}/compare/results?baseline=${baseline}&candidate=${candidate}`),
}

/**
 * The line of a JSON text where a member is, for a path such as
 * `operations[1].latencyMs.p95` or `metrics.vellum_op0_duration.p(95)`. The path is what a
 * validation error of an import names. Null when the text does not have the first member.
 * When a later member is missing, the line of the last member that was found.
 */
export function lineOfMember(text: string, path: string): number | null {
  let at = 0
  let found = false
  for (const part of path.split('.')) {
    const match = /^(.*?)((?:\[\d+\])*)$/.exec(part)!
    const name = text.indexOf(JSON.stringify(match[1]), at)
    if (name < 0) break
    at = name
    found = true
    for (const index of match[2].matchAll(/\[(\d+)\]/g)) {
      const element = elementStart(text, text.indexOf('[', at), Number(index[1]))
      // The array has no such element: the line of the array is the nearest cause.
      if (element < 0) return text.slice(0, at).split('\n').length
      at = element
    }
  }
  return found ? text.slice(0, at).split('\n').length : null
}

/** The position where element `index` of the array that opens at `open` starts, or -1. */
function elementStart(text: string, open: number, index: number): number {
  if (open < 0) return -1
  let depth = 0
  let inString = false
  let element = 0
  let expectValue = false
  for (let i = open; i < text.length; i++) {
    const c = text[i]
    if (inString) {
      if (c === '\\') i++
      else if (c === '"') inString = false
      continue
    }
    if (depth === 1 && expectValue && !/\s/.test(c)) {
      if (c === ']') return -1
      if (element === index) return i
      expectValue = false
    }
    if (c === '"') inString = true
    else if (c === '[' || c === '{') {
      depth++
      if (depth === 1) expectValue = true
    } else if (c === ']' || c === '}') {
      depth--
      if (depth === 0) return -1
    } else if (c === ',' && depth === 1) {
      element++
      expectValue = true
    }
  }
  return -1
}

/** The name a stress profile gives to a measured result as the source of a measured value. */
export const measurementRef = (resultId: string) => `k6:${resultId}`

export const METRIC_NAMES: Record<string, string> = {
  latencyP50: 'Latency, p50', latencyP90: 'Latency, p90', latencyP95: 'Latency, p95', latencyP99: 'Latency, p99',
  throughput: 'Throughput, per second', unexpectedStatusRate: 'Unexpected status',
}
