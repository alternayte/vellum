/**
 * The scenario document as the engine reads it (Modules/Trace/Engine/Scenario.cs), and the
 * pure edits that the step editor makes to it. The server stays the authority: it checks
 * every reference, payload and schema when the scenario plays. These helpers only keep the
 * document complete (every required list and map present) and the step IDs consistent.
 */

export type Outcome = 'Succeeded' | 'Failed' | 'Skipped'
export const OUTCOMES: Outcome[] = ['Succeeded', 'Failed', 'Skipped']

export type FaultKind = 'Error' | 'Timeout' | 'Outage'
export const FAULT_KINDS: FaultKind[] = ['Error', 'Timeout', 'Outage']

export const EVENT_KINDS = [
  'RequestSent', 'ResponseReceived', 'RequestFailed', 'RequestTimedOut', 'RetryScheduled',
  'Published', 'PublishFailed', 'Delivered', 'DeliveryFailed', 'DeadLettered', 'Dropped',
  'DataWritten', 'DataWriteFailed', 'BranchTaken', 'StepSkipped', 'Routed', 'NotRouted',
] as const
export type EventKind = (typeof EVENT_KINDS)[number]

export interface CallStep {
  type: 'call'
  id: string
  from: string
  operation: string
  request: unknown
  response: unknown
  then: Step[]
  onError: Step[]
}

export interface PublishStep {
  type: 'publish'
  id: string
  from: string
  channel: string
  message: string
  payload: unknown
  headers: Record<string, string>
  handlers: Record<string, Step[]>
  routingKey?: string
}

export interface WriteStep {
  type: 'write'
  id: string
  by: string
  store: string
  dataset: string
  change: unknown
}

export interface ParallelStep {
  type: 'parallel'
  id: string
  branches: Step[][]
}

export interface BranchStep {
  type: 'branch'
  id: string
  whenStep: string
  is: Outcome
  then: Step[]
  else: Step[]
}

export type Step = CallStep | PublishStep | WriteStep | ParallelStep | BranchStep
export type StepType = Step['type']
export const STEP_TYPES: StepType[] = ['call', 'publish', 'write', 'parallel', 'branch']

export interface Fault {
  target: string
  kind: FaultKind
  failFirst: number | null
}

export interface OutcomeAssertion {
  type: 'outcome'
  id: string
  step: string
  expected: Outcome
}

export interface EventCountAssertion {
  type: 'event-count'
  id: string
  kind: EventKind
  target: string | null
  expected: number
}

export type Assertion = OutcomeAssertion | EventCountAssertion

export interface ScenarioBody {
  steps: Step[]
  faults: Fault[]
  assertions: Assertion[]
}

const isObject = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown): string => (typeof value === 'string' ? value : '')
const json = (value: unknown): unknown => (value === undefined ? null : value)

/**
 * The member of `names` that `value` names in any letter case, as the server's enum converter
 * reads it, written in the member's own case. Anything else is `undefined`.
 */
const member = <T extends string>(names: readonly T[], value: unknown): T | undefined =>
  typeof value === 'string' ? names.find((name) => name.toLowerCase() === value.toLowerCase()) : undefined

/** A whole number, or a string of one: the server reads a number from a string too. */
const integer = (value: unknown): number | undefined => {
  if (typeof value === 'number') return Number.isInteger(value) ? value : undefined
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number(value.trim())
  return undefined
}

const listed = (names: readonly string[]): string =>
  names.length <= 3 ? `${names.slice(0, -1).join(', ')} or ${names[names.length - 1]}` : `one of ${names.join(', ')}`

/**
 * Reads a document that may be partial or written by hand into the shape the editor shows.
 * A missing list or map becomes empty and a missing reference becomes "". A step or an
 * assertion of a type the engine does not know, an enum name the server does not read, a count
 * that is not a whole number and a fault that is not an object are returned in `unknown`, so
 * the editor can say so instead of changing or dropping them silently. An enum name in another
 * letter case is read as the server reads it.
 */
export function readBody(raw: { steps?: unknown; faults?: unknown; assertions?: unknown }): { body: ScenarioBody; unknown: string[] } {
  const unknown: string[] = []
  // A value the editor cannot show as written is named in `unknown`, so the JSON tab keeps it
  // and blocks the save. The default it gets here is never saved.
  const enumValue = <T extends string>(names: readonly T[], value: unknown, fallback: T, what: string): T => {
    if (value === undefined) return fallback
    const found = member(names, value)
    if (found) return found
    unknown.push(`${what} ${JSON.stringify(value)} (use ${listed(names)})`)
    return fallback
  }
  const outcome = (value: unknown, what: string): Outcome => enumValue(OUTCOMES, value, 'Succeeded', what)
  const count = (value: unknown, fallback: number | null, what: string): number | null => {
    if (value === undefined || value === null) return fallback
    const found = integer(value)
    if (found !== undefined) return found
    unknown.push(`${what} ${JSON.stringify(value)} (use a whole number)`)
    return fallback
  }
  const reference = (value: unknown, what: string): string | null => {
    if (value === undefined || value === null || value === '') return null
    if (typeof value === 'string') return value
    unknown.push(`${what} ${JSON.stringify(value)} (use an ID)`)
    return null
  }
  const steps = (items: unknown): Step[] => {
    const out: Step[] = []
    for (const item of Array.isArray(items) ? items : []) {
      const step = readStep(item)
      if (step) out.push(step)
      else unknown.push(isObject(item) ? `step ${text(item.id) || '(no ID)'} of type "${text(item.type)}"` : 'an entry that is not a step')
    }
    return out
  }
  const readStep = (item: unknown): Step | null => {
    if (!isObject(item)) return null
    const id = text(item.id)
    switch (item.type) {
      case 'call':
        return { type: 'call', id, from: text(item.from), operation: text(item.operation), request: json(item.request), response: json(item.response), then: steps(item.then), onError: steps(item.onError) }
      case 'publish': {
        const handlers: Record<string, Step[]> = {}
        if (isObject(item.handlers)) for (const [key, list] of Object.entries(item.handlers)) handlers[key] = steps(list)
        const headers: Record<string, string> = {}
        if (isObject(item.headers)) for (const [key, value] of Object.entries(item.headers)) headers[key] = text(value)
        const step: PublishStep = { type: 'publish', id, from: text(item.from), channel: text(item.channel), message: text(item.message), payload: json(item.payload), headers, handlers }
        if (typeof item.routingKey === 'string') step.routingKey = item.routingKey
        return step
      }
      case 'write':
        return { type: 'write', id, by: text(item.by), store: text(item.store), dataset: text(item.dataset), change: json(item.change) }
      case 'parallel':
        return { type: 'parallel', id, branches: (Array.isArray(item.branches) ? item.branches : []).map(steps) }
      case 'branch':
        return { type: 'branch', id, whenStep: text(item.whenStep), is: outcome(item.is, `the outcome of branch ${id || '(no ID)'}`), then: steps(item.then), else: steps(item.else) }
      default:
        return null
    }
  }

  const topSteps = steps(raw.steps)
  const faults: Fault[] = []
  for (const [i, f] of (Array.isArray(raw.faults) ? raw.faults : []).entries()) {
    if (!isObject(f)) {
      unknown.push(`fault ${i + 1}, which is not an object`)
      continue
    }
    const what = `fault ${i + 1}`
    faults.push({
      target: reference(f.target, `the target of ${what}`) ?? '',
      kind: enumValue(FAULT_KINDS, f.kind, 'Error', `the kind of ${what}`),
      failFirst: count(f.failFirst, null, `failFirst of ${what}`),
    })
  }

  const assertions: Assertion[] = []
  for (const a of Array.isArray(raw.assertions) ? raw.assertions : []) {
    const what = isObject(a) ? `assertion ${text(a.id) || '(no ID)'}` : ''
    if (isObject(a) && a.type === 'outcome') assertions.push({ type: 'outcome', id: text(a.id), step: text(a.step), expected: outcome(a.expected, `the expected outcome of ${what}`) })
    else if (isObject(a) && a.type === 'event-count') {
      assertions.push({
        type: 'event-count', id: text(a.id),
        kind: enumValue(EVENT_KINDS, a.kind, 'DeadLettered', `the event kind of ${what}`),
        target: reference(a.target, `the target of ${what}`),
        expected: count(a.expected, 0, `the expected count of ${what}`) ?? 0,
      })
    } else unknown.push(isObject(a) ? `assertion ${text(a.id) || '(no ID)'} of type "${text(a.type)}"` : 'an entry that is not an assertion')
  }

  return { body: { steps: topSteps, faults, assertions }, unknown }
}

/** The step lists inside a step, with what each one means. */
export function childLists(step: Step): { key: string; steps: Step[] }[] {
  switch (step.type) {
    case 'call': return [{ key: 'then', steps: step.then }, { key: 'onError', steps: step.onError }]
    case 'publish': return Object.entries(step.handlers).map(([key, steps]) => ({ key: `handlers.${key}`, steps }))
    case 'parallel': return step.branches.map((steps, i) => ({ key: `branches.${i}`, steps }))
    case 'branch': return [{ key: 'then', steps: step.then }, { key: 'else', steps: step.else }]
    default: return []
  }
}

/** Every step ID in the document, nested ones too, in the order they are written. */
export function allStepIds(steps: Step[]): string[] {
  return steps.flatMap((step) => [step.id, ...childLists(step).flatMap((list) => allStepIds(list.steps))])
}

/** A step ID that the document does not use yet: the type and the next free number. */
export function nextStepId(type: StepType, used: string[]): string {
  const taken = new Set(used)
  for (let n = 1; ; n++) {
    const id = `${type}-${n}`
    if (!taken.has(id)) return id
  }
}

export function nextAssertionId(used: string[]): string {
  const taken = new Set(used)
  for (let n = 1; ; n++) {
    const id = `expect-${n}`
    if (!taken.has(id)) return id
  }
}

/** A new step of the type with every required list and map, so the server accepts its shape. */
export function newStep(type: StepType, id: string): Step {
  switch (type) {
    case 'call': return { type, id, from: '', operation: '', request: null, response: null, then: [], onError: [] }
    case 'publish': return { type, id, from: '', channel: '', message: '', payload: null, headers: {}, handlers: {} }
    case 'write': return { type, id, by: '', store: '', dataset: '', change: null }
    case 'parallel': return { type, id, branches: [[], []] }
    case 'branch': return { type, id, whenStep: '', is: 'Failed', then: [], else: [] }
  }
}

/**
 * The step IDs whose outcome is final when each step of `steps` starts, as the engine's
 * validator counts them: the steps before it in the same list and what they nest, except
 * consumer handlers, plus `inherited` from the enclosing lists. A branch may read only these.
 */
export function finalBefore(steps: Step[], inherited: string[]): string[][] {
  const completed = [...inherited]
  const add = (step: Step) => {
    completed.push(step.id)
    if (step.type === 'publish') return
    for (const list of childLists(step)) list.steps.forEach(add)
  }
  return steps.map((step) => {
    const before = [...completed]
    add(step)
    return before
  })
}

/** Renames a step and every branch condition and outcome assertion that names it. */
export function renameStep(body: ScenarioBody, from: string, to: string): ScenarioBody {
  const rename = (steps: Step[]): Step[] => steps.map((step) => {
    const id = step.id === from ? to : step.id
    switch (step.type) {
      case 'call': return { ...step, id, then: rename(step.then), onError: rename(step.onError) }
      case 'publish': return { ...step, id, handlers: Object.fromEntries(Object.entries(step.handlers).map(([k, v]) => [k, rename(v)])) }
      case 'parallel': return { ...step, id, branches: step.branches.map(rename) }
      case 'branch': return { ...step, id, whenStep: step.whenStep === from ? to : step.whenStep, then: rename(step.then), else: rename(step.else) }
      default: return { ...step, id }
    }
  })
  return {
    ...body,
    steps: rename(body.steps),
    assertions: body.assertions.map((a) => (a.type === 'outcome' && a.step === from ? { ...a, step: to } : a)),
  }
}

/** Moves the item at `index` one place up (-1) or down (+1). */
export function move<T>(items: T[], index: number, by: -1 | 1): T[] {
  const to = index + by
  if (to < 0 || to >= items.length) return items
  const next = [...items]
  ;[next[index], next[to]] = [next[to], next[index]]
  return next
}

/** The document as the JSON tab shows it and the server stores it. */
export function bodyText(body: ScenarioBody): string {
  return JSON.stringify(body, null, 2)
}
