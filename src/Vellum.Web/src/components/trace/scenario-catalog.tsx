import { createContext, useContext, useMemo } from 'react'
import { useBindings, useChannels, useOperations, useSubscriptions, type Binding, type Channel, type Operation, type Subscription } from '@/hooks/use-contracts'
import { useMessages, type Message } from '@/hooks/use-messages'
import { useSchemas } from '@/hooks/use-schemas'
import { reachableChannels } from '@/lib/trace'
import type { Step } from '@/lib/scenario-document'
import type { PickerOption } from './reference-picker'
import type { ScenarioReference } from './scenario-editor'

/** What the step editor offers: every part of the model a scenario can name, by name. */
export interface ScenarioCatalog {
  elements: PickerOption[]
  operations: PickerOption[]
  channels: PickerOption[]
  messages: PickerOption[]
  /** Fault targets: operations, channels, subscriptions and elements, in groups. */
  faultTargets: PickerOption[]
  /** Event-count targets: the fault targets and the bindings. */
  eventTargets: PickerOption[]
  operation: (id: string) => Operation | undefined
  message: (id: string) => Message | undefined
  channel: (id: string) => Channel | undefined
  subscription: (id: string) => Subscription | undefined
  schemaName: (id: string | null) => string | null
  /** The name of any ID the document can hold, or null. */
  name: (id: string) => string | null
  /** The subscriptions that a publish to the channel reaches, through its bindings too. */
  reached: (channelId: string) => { subscription: Subscription; label: string; detail: string }[]
}

const empty: ScenarioCatalog = {
  elements: [], operations: [], channels: [], messages: [], faultTargets: [], eventTargets: [],
  operation: () => undefined, message: () => undefined, channel: () => undefined, subscription: () => undefined,
  schemaName: () => null, name: () => null, reached: () => [],
}

export const CatalogContext = createContext<ScenarioCatalog>(empty)
export const useCatalog = () => useContext(CatalogContext)

/** What every step editor needs from the editor around it. */
export interface StepEditing {
  /** Every step ID in the document, for new IDs and the duplicate check. */
  allIds: string[]
  /** Renames a step and what names it. Returns the reason when the ID cannot be used. */
  rename: (from: string, to: string) => string | null
  /** The server's problems with each step, by step ID. */
  issues: Record<string, string[]>
  /** A JSON field reports whether its text parses. */
  reportJson: (key: string, invalid: boolean) => void
  /** What the step with this ID does, in one line. */
  stepSummary: (id: string) => string | undefined
  /** The saved scenario whose steps take comment threads. Null for a scenario that is not saved yet. */
  comments?: { projectId: string; scenarioId: string; scenarioName: string; savedStepIds: string[] } | null
}

export const StepEditingContext = createContext<StepEditing>({ allIds: [], rename: () => null, issues: {}, reportJson: () => {}, stepSummary: () => undefined })

const attempts = (n: number) => `${n} ${n === 1 ? 'attempt' : 'attempts'}`

export function useScenarioCatalog(projectId: string, branchId: string | null | undefined, references: ScenarioReference[]): ScenarioCatalog {
  const { data: operations = [] } = useOperations(projectId, branchId)
  const { data: channels = [] } = useChannels(projectId, branchId)
  const { data: subscriptions = [] } = useSubscriptions(projectId, branchId)
  const { data: bindings = [] } = useBindings(projectId, branchId)
  const { data: messages = [] } = useMessages(projectId, branchId ?? undefined)
  const { data: schemas = [] } = useSchemas(projectId)

  return useMemo(() => {
    const elementRefs = references.filter((r) => r.group === 'Elements')
    const elementName = new Map(elementRefs.map((e) => [e.id, e.label]))
    const channelName = new Map(channels.map((c) => [c.id, c.name]))
    const el = (id: string) => elementName.get(id) ?? 'Unknown element'
    const ch = (id: string) => channelName.get(id) ?? 'Unknown channel'
    const subscriptionLabel = (s: Subscription) => `${el(s.consumerId)} on ${ch(s.channelId)}`
    const subscriptionDetail = (s: Subscription) =>
      `${attempts(s.retry.maxAttempts)}${s.retry.backoffMs ? `, ${s.retry.backoffMs} ms apart` : ''} · ${s.deadLetterChannelId ? `dead letters to ${ch(s.deadLetterChannelId)}` : 'no dead-letter channel'}`
    const bindingLabel = (b: Binding) => `${ch(b.sourceChannelId)} → ${ch(b.targetChannelId)}`

    const elementOptions: PickerOption[] = elementRefs
      .map((e) => ({ id: e.id, label: e.label, detail: e.kind }))
      .sort((a, b) => a.label.localeCompare(b.label))
    const operationOptions: PickerOption[] = operations
      .map((o) => ({
        id: o.id, label: o.name, group: el(o.ownerId),
        detail: `${o.transport} · ${o.timeoutMs ? `times out after ${o.timeoutMs} ms` : 'no timeout'} · ${attempts(o.retry.maxAttempts)}`,
      }))
      .sort((a, b) => a.group.localeCompare(b.group) || a.label.localeCompare(b.label))
    const channelOptions: PickerOption[] = channels.map((c) => ({ id: c.id, label: c.name, detail: c.kind })).sort((a, b) => a.label.localeCompare(b.label))
    const messageOptions: PickerOption[] = messages.map((m) => ({ id: m.id, label: m.name, detail: `produced by ${el(m.producerId)}` })).sort((a, b) => a.label.localeCompare(b.label))
    const subscriptionOptions: PickerOption[] = subscriptions.map((s) => ({ id: s.id, label: subscriptionLabel(s), detail: subscriptionDetail(s), group: 'Subscriptions' }))
    const faultTargets = [
      ...operationOptions.map((o) => ({ ...o, detail: `${o.group} · ${o.detail}`, group: 'Operations' })),
      ...channelOptions.map((o) => ({ ...o, group: 'Channels' })),
      ...subscriptionOptions,
      ...elementOptions.map((o) => ({ ...o, group: 'Elements' })),
    ]
    const eventTargets = [
      ...faultTargets,
      ...bindings.map((b) => ({ id: b.id, label: bindingLabel(b), detail: b.routingKey ? `routing key ${b.routingKey}` : 'every message', group: 'Bindings' })),
    ]

    const names = new Map<string, string>([
      ...elementName,
      ...operations.map((o) => [o.id, o.name] as const),
      ...channelName,
      ...messages.map((m) => [m.id, m.name] as const),
      ...subscriptions.map((s) => [s.id, subscriptionLabel(s)] as const),
      ...bindings.map((b) => [b.id, bindingLabel(b)] as const),
    ])

    return {
      elements: elementOptions,
      operations: operationOptions,
      channels: channelOptions,
      messages: messageOptions,
      faultTargets,
      eventTargets,
      operation: (id) => operations.find((o) => o.id === id),
      message: (id) => messages.find((m) => m.id === id),
      channel: (id) => channels.find((c) => c.id === id),
      subscription: (id) => subscriptions.find((s) => s.id === id),
      schemaName: (id) => {
        if (!id) return null
        const schema = schemas.find((s) => s.id === id)
        return schema ? `${schema.name} v${schema.version}` : 'a schema that is not loaded'
      },
      name: (id) => names.get(id) ?? null,
      reached: (channelId) => {
        const order = reachableChannels(channelId, bindings)
        return subscriptions
          .filter((s) => order.includes(s.channelId))
          .sort((a, b) => order.indexOf(a.channelId) - order.indexOf(b.channelId))
          .map((s) => ({ subscription: s, label: subscriptionLabel(s), detail: subscriptionDetail(s) }))
      },
    }
  }, [references, operations, channels, subscriptions, bindings, messages, schemas])
}

/** One line that says what a step does, with names from the model. */
export function describeStep(step: Step, catalog: ScenarioCatalog): string {
  const n = (id: string, what: string) => (id ? (catalog.name(id) ?? `an unknown ${what}`) : `no ${what} yet`)
  switch (step.type) {
    case 'call': return `${n(step.from, 'caller')} calls ${n(step.operation, 'operation')}`
    case 'publish': return `${n(step.from, 'publisher')} publishes ${n(step.message, 'message')} to ${n(step.channel, 'channel')}`
    case 'write': return `${n(step.by, 'writer')} writes ${step.dataset || 'a dataset'} in ${n(step.store, 'store')}`
    case 'parallel': return `${step.branches.length} branches at the same time`
    case 'branch': return `if ${step.whenStep || 'a step'} is ${step.is.toLowerCase()}`
  }
}
