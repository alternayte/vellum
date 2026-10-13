import { ApiError } from '@/lib/settings-api'

/** Typed access to the stress endpoints. Every number of a result is an estimate from a model. */

export type AssumptionLabel = 'declared' | 'measured'
export type ArrivalKind = 'constant' | 'poisson' | 'ramp' | 'burst'
export type DistributionKind = 'fixed' | 'exponential' | 'lognormal' | 'uniform'

interface Labelled {
  label?: AssumptionLabel
  measurementRef?: string | null
}

export interface BurstWindow { fromSeconds: number; toSeconds: number; ratePerSecond: number }

export interface Arrivals extends Labelled {
  kind: ArrivalKind
  ratePerSecond: number
  toRatePerSecond: number | null
  bursts: BurstWindow[] | null
  durationSeconds: number
  warmUpSeconds: number
}

export interface Distribution {
  kind: DistributionKind
  ms?: number | null
  meanMs?: number | null
  medianMs?: number | null
  p95Ms?: number | null
  minMs?: number | null
  maxMs?: number | null
}

export interface ResourceAssumption extends Labelled { elementId: string; concurrency: number; queueCapacity: number | null }
export interface ServiceTimeAssumption extends Labelled { target: string; time: Distribution }
export interface SubscriptionAssumption extends Labelled { subscriptionId: string; consumers: number; queueCapacity: number | null }
export interface BranchAssumption extends Labelled { stepId: string; probability: number }
export interface PolicyOverride extends Labelled { target: string; timeoutMs: number | null; maxAttempts: number | null; backoffMs: number | null }
export interface Outage { target: string; fromSeconds: number; toSeconds: number }

export interface StressProfile {
  name: string
  scenarioId: string
  /** An unsigned 64-bit integer in decimal digits. */
  seed: string
  arrivals: Arrivals
  resources: ResourceAssumption[]
  serviceTimes: ServiceTimeAssumption[]
  subscriptions: SubscriptionAssumption[]
  branches: BranchAssumption[]
  overrides: PolicyOverride[]
  outages: Outage[]
}

export interface StressProfileSummary { id: string; name: string; scenarioId: string; version: number; updatedAt: string; runs: number }
/** A measured service time and whether the k6 result that it names is stale now. */
export interface ProfileMeasurement { target: string; measurementRef: string; measurementStale: boolean; staleBecause: ('model' | 'scenario' | 'missing')[] }
export interface StressProfileDetail { id: string; version: number; updatedAt: string; profile: StressProfile; measurements: ProfileMeasurement[] }

export interface StressRequirements {
  scenarioIssues: { stepId: string; code: string; message: string }[]
  resources: { elementId: string; name: string }[]
  serviceTimes: { target: string; kind: 'operation' | 'subscription' | 'store'; name: string; timeoutMs: number | null; maxAttempts: number | null; backoffMs: number | null }[]
  subscriptions: { subscriptionId: string; name: string; maxAttempts: number; backoffMs: number; deadLetters: boolean }[]
  branches: string[]
  outageTargets: { id: string; kind: string; name: string }[]
  scenarioFaults: number
}

export interface Counts { completed: number; failed: number; rejected: number; timedOut: number; dropped: number; deadLettered: number }

export interface LatencySummary {
  label: string
  count: number
  kept: number
  reservoir: boolean
  minMs: number | null
  meanMs: number | null
  p50Ms: number | null
  p90Ms: number | null
  p95Ms: number | null
  p99Ms: number | null
  maxMs: number | null
}

/** A time in which work waited at a resource without a break. `toMs` is null when work still waited at the end of the run. */
export interface BacklogWindow { fromMs: number; toMs: number | null; peakDepth: number; peakAtMs: number }

/** What the saturation rule found for one resource. `queueGrows` is a statement of its own. */
export interface Saturation {
  saturated: boolean
  turnedAway: number
  firstTurnedAwayMs: number | null
  lastTurnedAwayMs: number | null
  arrivals: number
  arrivalVariation: number
  serviceVariation: number
  noiseBound: number
  windows: BacklogWindow[]
  moreWindows: boolean
  depthAtMidWindow: number
  queueGrows: boolean
  growthPerSecond: number | null
}

export interface ResourceStats {
  label: string
  id: string
  name: string
  kind: 'element' | 'subscription'
  workers: number
  queueCapacity: number | null
  arrived: number
  waited: number
  counts: Counts
  retries: number
  overflowed: number
  wastedCount: number
  wastedMs: number
  utilisation: number
  throughputPerSecond: number
  queue: { meanDepth: number; meanDepthByQuarter: number[]; maxDepth: number; depthAtEndOfArrivals: number }
  wait: LatencySummary
  latency: LatencySummary
  /** Calls that held a worker and failed because a step inside them failed. Null in a result of engine version 1. */
  failedInside: number | null
  /** Null in a result of engine version 1. */
  saturation: Saturation | null
}

export interface OperationStats {
  label: string
  id: string
  name: string
  ownerId: string
  timeoutMs: number
  maxAttempts: number
  backoffMs: number
  attempts: number
  counts: Counts
  retries: number
  exhausted: number
  wasted: number
  throughputPerSecond: number
  latency: LatencySummary
}

export interface ResourceSeries { id: string; name: string; kind: string; queueDepth: number[]; inService: number[] }

export interface StressResult {
  engineVersion: string
  label: string
  seed: string
  window: { warmUpMs: number; durationMs: number; endedAtMs: number }
  flow: { label: string; arrivals: number; counts: Counts; throughputPerSecond: number; latency: LatencySummary }
  elements: ResourceStats[]
  operations: OperationStats[]
  subscriptions: ResourceStats[]
  channels: { label: string; id: string; name: string; published: number; publishFailed: number }[]
  series: {
    label: string
    intervalMs: number
    points: number
    truncated: boolean
    arrivalsPerSecond: number[]
    throughputPerSecond: number[]
    failuresPerSecond: number[]
    inFlight: number[]
    resources: ResourceSeries[]
  }
  drain: { label: string; drained: boolean; timeToDrainMs: number | null; leftInSystem: number }
  bottleneck: {
    label: string
    found: boolean
    id: string | null
    name: string | null
    kind: string | null
    utilisation: number | null
    /** Every saturated resource in the order of the rule. Null in a result of engine version 1. */
    saturated: string[] | null
    rule: string
    growthRule: string | null
    /** Only a result of engine version 1 has the next three. */
    meanDepthByQuarter?: number[] | null
    depthAtEndOfArrivals?: number | null
    growthPerSecond?: number | null
  }
  /** `measurementStale`: for a measured value, whether its k6 result was stale for this run. Absent for a declared value and in a result of engine version 1 or 2. */
  assumptions: { group: string; target: string | null; name: string; field: string; value: string; unit: string; label: AssumptionLabel; measurementRef: string | null; measurementStale?: boolean | null }[]
  overrides: { target: string; name: string; field: string; modelValue: number; profileValue: number; label: AssumptionLabel; measurementRef: string | null }[]
  unmodelled: { code: string; kind: 'fixed' | 'run'; text: string }[]
  confidence: { level: 'low' | 'medium'; rule: string; reasons: string[] }
  methods: { percentiles: string; counting: string; sampling: string; time: string }
  /** The limits that the run used. They are inputs of the result. Null in a result of engine version 1. */
  limits: {
    maxDurationSeconds: number
    maxDrainSeconds: number
    maxArrivals: number
    maxEvents: number
    maxQueued: number
    maxPendingEvents: number
    maxSamples: number
    maxSeriesPoints: number
    maxTargets: number
  } | null
  hash: string
}

export interface StressRunSummary {
  id: string
  profileId: string
  profileVersion: number
  currentProfileVersion: number
  modelStreamId: string
  modelVersion: number
  scenarioId: string
  scenarioVersion: number
  seed: string
  engineVersion: string
  hash: string
  at: string
  summary: {
    label: string
    arrivals: number
    completed: number
    failed: number
    throughputPerSecond: number
    p50Ms: number | null
    p95Ms: number | null
    p99Ms: number | null
    bottleneck: string | null
    confidence: string
    drained: boolean
  }
  outdated: boolean
  outdatedBecause: ('model' | 'scenario')[]
}

export interface StressRun { run: StressRunSummary; profile: StressProfile; result: StressResult }

export interface StressIssue { code: string; target: string; message: string; limit: string | null }

export interface StressComparison {
  label: string
  left: StressRunSummary
  right: StressRunSummary
  metrics: { scope: string; target: string | null; name: string; metric: string; unit: string; left: number | null; right: number | null; delta: number | null }[]
  inputs: { input: string; target: string | null; name: string; left: string | null; right: string | null }[]
  note: string
  /** True only when the two runs have the same result hash. */
  sameResult: boolean
}

/** A run that was refused or that passed a limit. Nothing was stored; the issues say why. */
export class StressRefused extends Error {
  issues: StressIssue[]
  tooLarge: boolean

  constructor(title: string, type: string, issues: StressIssue[]) {
    super(title)
    this.issues = issues
    this.tooLarge = type === 'stress-too-large'
  }
}

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await response.text()
  let data: unknown
  try {
    data = text ? JSON.parse(text) : null
  } catch {
    // A body that is not JSON has nothing to show; the status decides.
  }
  if (response.ok) return data as T

  const payload = (data ?? {}) as { type?: string; title?: string; detail?: string | null; errors?: { field: string; message: string }[] | null; issues?: StressIssue[] }
  if (response.status === 422 && Array.isArray(payload.issues))
    throw new StressRefused(payload.title ?? 'The run was refused.', payload.type ?? 'stress-refused', payload.issues)
  throw new ApiError(
    response.status,
    payload.title
      ? (payload.detail ? `${payload.title}. ${payload.detail}` : payload.title)
      : response.status === 403 ? 'You do not have permission to do this.' : 'The request did not complete.',
    Array.isArray(payload.errors) ? payload.errors : [],
  )
}

const base = (projectId: string) => `/api/projects/${projectId}/stress`
const branch = (branchId?: string | null, first = true) => (branchId ? `${first ? '?' : '&'}branchId=${branchId}` : '')

export const stressApi = {
  profiles: (projectId: string) => call<StressProfileSummary[]>('GET', `${base(projectId)}/profiles`),
  defaults: (projectId: string) => call<StressProfileDefaults>('GET', `${base(projectId)}/defaults`),
  profile: (projectId: string, id: string) => call<StressProfileDetail>('GET', `${base(projectId)}/profiles/${id}`),
  requirements: (projectId: string, scenarioId: string, branchId?: string | null) =>
    call<StressRequirements>('GET', `${base(projectId)}/requirements?scenarioId=${scenarioId}${branch(branchId, false)}`),
  create: (projectId: string, profile: StressProfile) => call<StressProfileDetail>('POST', `${base(projectId)}/profiles`, profile),
  update: (projectId: string, id: string, profile: StressProfile) => call<StressProfileDetail>('PUT', `${base(projectId)}/profiles/${id}`, profile),
  remove: (projectId: string, id: string) => call<void>('DELETE', `${base(projectId)}/profiles/${id}`),
  run: (projectId: string, id: string, branchId?: string | null) => call<StressRun>('POST', `${base(projectId)}/profiles/${id}/runs${branch(branchId)}`),
  runs: (projectId: string, id: string) => call<StressRunSummary[]>('GET', `${base(projectId)}/profiles/${id}/runs`),
  getRun: (projectId: string, runId: string) => call<StressRun>('GET', `${base(projectId)}/runs/${runId}`),
  removeRun: (projectId: string, runId: string) => call<void>('DELETE', `${base(projectId)}/runs/${runId}`),
  compare: (projectId: string, left: string, right: string) => call<StressComparison>('GET', `${base(projectId)}/compare?left=${left}&right=${right}`),
}

/** What a new profile starts with: the workspace's simulation settings (Settings, Simulation). */
export interface StressProfileDefaults {
  seed: string
  durationSeconds: number
  warmUpSeconds: number
  arrivalRatePerSecond: number
}

/**
 * A profile for a scenario with no input filled in: the form asks for each one. The seed,
 * the window and the arrival rate come from the workspace's simulation settings.
 */
export function emptyProfile(scenarioId: string, defaults: StressProfileDefaults): StressProfile {
  return {
    name: '',
    scenarioId,
    seed: defaults.seed,
    arrivals: {
      kind: 'poisson', ratePerSecond: defaults.arrivalRatePerSecond, toRatePerSecond: null, bursts: null,
      durationSeconds: defaults.durationSeconds, warmUpSeconds: defaults.warmUpSeconds,
    },
    resources: [],
    serviceTimes: [],
    subscriptions: [],
    branches: [],
    overrides: [],
    outages: [],
  }
}

/** Milliseconds with three significant digits, or a dash for no value. */
export function formatMs(value: number | null | undefined): string {
  if (value === null || value === undefined) return '–'
  if (value >= 10_000) return `${(value / 1000).toFixed(1)} s`
  if (value >= 100) return `${value.toFixed(0)} ms`
  if (value >= 10) return `${value.toFixed(1)} ms`
  return `${value.toFixed(2)} ms`
}

export function formatNumber(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined) return '–'
  return Number.isInteger(value) ? value.toLocaleString('en-US') : value.toLocaleString('en-US', { maximumFractionDigits: digits })
}

export const formatPercent = (value: number | null | undefined) => (value === null || value === undefined ? '–' : `${(value * 100).toFixed(1)} %`)

/** Round steps of 1, 2 or 5 times a power of ten that cover 0 to max in about `count` steps. */
export function niceTicks(max: number, count = 4): number[] {
  if (!(max > 0)) return [0, 1]
  const rough = max / count
  const power = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 5, 10].map((m) => m * power).find((s) => s >= rough) ?? 10 * power
  const ticks: number[] = []
  for (let value = 0; value < max + step - 1e-9; value += step) ticks.push(Number(value.toPrecision(12)))
  return ticks
}
