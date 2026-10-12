export type TraceEventKind =
  | 'RequestSent'
  | 'ResponseReceived'
  | 'RequestFailed'
  | 'RequestTimedOut'
  | 'RetryScheduled'
  | 'Published'
  | 'PublishFailed'
  | 'Delivered'
  | 'DeliveryFailed'
  | 'DeadLettered'
  | 'Dropped'
  | 'DataWritten'
  | 'DataWriteFailed'
  | 'BranchTaken'
  | 'StepSkipped'

export interface TraceEvent {
  sequence: number
  atMs: number
  kind: TraceEventKind
  stepId: string
  attempt: number
  from: string | null
  to: string | null
  target: string | null
  payload: unknown
  detail: string | null
  message: string | null
  headers: Record<string, string> | null
}

export interface TraceAssertionResult {
  id: string
  passed: boolean
  detail: string
}

export interface TraceIssue {
  stepId: string
  code: string
  message: string
}

export interface TraceRun {
  engineVersion: string
  hash: string
  issues: TraceIssue[]
  events: TraceEvent[]
  outcomes: Record<string, 'Succeeded' | 'Failed' | 'Skipped'>
  assertions: TraceAssertionResult[]
  /** Display names for every element, operation, channel and subscription ID in the run. */
  labels: Record<string, string>
}

/**
 * What an event means, which decides its colour and motion. Colour is never the only
 * carrier: every event also has a sentence from `describeEvent`.
 */
export type TraceFamily = 'request' | 'event' | 'data' | 'retry' | 'failure' | 'control'

const FAMILY: Record<TraceEventKind, TraceFamily> = {
  RequestSent: 'request',
  ResponseReceived: 'request',
  Published: 'event',
  Delivered: 'event',
  DataWritten: 'data',
  RetryScheduled: 'retry',
  RequestFailed: 'failure',
  RequestTimedOut: 'failure',
  PublishFailed: 'failure',
  DeliveryFailed: 'failure',
  DeadLettered: 'failure',
  Dropped: 'failure',
  DataWriteFailed: 'failure',
  BranchTaken: 'control',
  StepSkipped: 'control',
}

export function traceFamily(kind: TraceEventKind): TraceFamily {
  return FAMILY[kind]
}

export function traceColor(kind: TraceEventKind): string {
  return `var(--trace-${FAMILY[kind]})`
}

export function formatVirtualTime(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(ms % 1000 === 0 ? 0 : 2)} s`
}

export function describeEvent(event: TraceEvent, labels: Record<string, string>): string {
  const name = (id: string | null) => (id ? (labels[id] ?? 'an unknown part') : 'an unknown part')
  const from = name(event.from)
  const to = name(event.to)
  const target = name(event.target)
  const attempt = event.attempt > 1 ? ` (attempt ${event.attempt})` : ''
  const message = event.message ? (labels[event.message] ?? 'the message') : 'the message'

  switch (event.kind) {
    case 'RequestSent':
      return `${from} calls ${target} on ${to}${attempt}`
    case 'ResponseReceived':
      return `${from} responds to ${to} for ${target}`
    case 'RequestFailed':
      return `${target} on ${to} fails${attempt}`
    case 'RequestTimedOut':
      return `${target} on ${to} times out${attempt}`
    case 'RetryScheduled':
      return `Retry ${event.attempt} of ${target} is scheduled`
    case 'Published':
      return `${from} publishes ${message === 'the message' ? 'a message' : message} to ${target}`
    case 'PublishFailed':
      return `${from} cannot publish ${message === 'the message' ? 'a message' : message} to ${target}`
    case 'Delivered':
      return `${to} receives ${message}${attempt}`
    case 'DeliveryFailed':
      return `${to} fails to handle ${message}${attempt}`
    case 'DeadLettered':
      return `${message === 'the message' ? 'The message' : message} for ${from} moves to ${target}`
    case 'Dropped':
      return `${message === 'the message' ? 'The message' : message} for ${from} is lost`
    case 'DataWritten':
      return `${from} writes ${event.detail ?? 'data'} in ${to}`
    case 'DataWriteFailed':
      return `${from} cannot write to ${to}`
    case 'BranchTaken':
      return `Step ${event.stepId} takes the ${event.detail} path`
    case 'StepSkipped':
      return `Step ${event.stepId} is skipped`
  }
}

/** A short label for the canvas: what is moving, not who is involved (the edge shows that). */
export function traceEdgeLabel(event: TraceEvent, labels: Record<string, string>): string {
  const target = event.target ? (labels[event.target] ?? '') : ''
  const message = event.message ? (labels[event.message] ?? 'message') : 'message'
  switch (event.kind) {
    case 'RequestSent':
      return event.attempt > 1 ? `${target} · attempt ${event.attempt}` : target
    case 'ResponseReceived':
      return `${target} · response`
    case 'RequestFailed':
      return `${target} · failed`
    case 'RequestTimedOut':
      return `${target} · timed out`
    case 'RetryScheduled':
      return `retry ${event.attempt}`
    case 'Delivered':
      return event.attempt > 1 ? `${message} · redelivery ${event.attempt}` : message
    case 'DeliveryFailed':
      return `${message} · not handled`
    case 'DataWritten':
      return `write ${event.detail ?? ''}`.trim()
    case 'DataWriteFailed':
      return 'write failed'
    default:
      return event.kind
  }
}
