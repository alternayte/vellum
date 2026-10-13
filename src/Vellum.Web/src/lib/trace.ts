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
  | 'Routed'
  | 'NotRouted'

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
  /** Display names for every element, operation, channel, subscription and binding ID in the run. */
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
  Routed: 'event',
  NotRouted: 'control',
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

/**
 * Why a binding was not followed, from the detail of a `NotRouted` event. The engine writes
 * the binding's ID first; the sentence already names both channels, so the ID is left out.
 */
export function notRoutedReason(detail: string | null): string {
  const reason = (detail ?? '').replace(/^Binding [0-9a-fA-F-]{36}:? /, '').replace(/\.$/, '')
  if (!reason) return 'the binding does not match'
  return reason.startsWith('needs ') ? `the binding ${reason}` : reason
}

/**
 * The channels a message published to `channelId` can reach through bindings, the channel
 * itself first. Routing keys are not read: the server accepts a handler for every
 * subscription on these channels, and a trace says which bindings a key closes.
 */
export function reachableChannels(
  channelId: string,
  bindings: { sourceChannelId: string; targetChannelId: string }[],
): string[] {
  const reached = [channelId]
  for (let i = 0; i < reached.length; i++) {
    for (const binding of bindings) {
      if (binding.sourceChannelId === reached[i] && !reached.includes(binding.targetChannelId)) reached.push(binding.targetChannelId)
    }
  }
  return reached
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
    case 'Routed':
      return `${from} routes ${message} to ${to}`
    case 'NotRouted':
      return `${from} does not route ${message} to ${to}: ${notRoutedReason(event.detail)}`
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
