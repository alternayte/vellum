/** Reading a difference between two models: what a changed fact is called and which of its fields differ. */

type Value = Record<string, unknown> | null | undefined

export const ENTITY_LABEL: Record<string, string> = {
  element: 'Elements',
  relationship: 'Relationships',
  message: 'Messages',
  operation: 'Operations',
  channel: 'Channels',
  subscription: 'Subscriptions',
  binding: 'Bindings',
  schema: 'Schemas',
}

export const ENTITY_ORDER = ['element', 'relationship', 'message', 'operation', 'channel', 'subscription', 'binding', 'schema']

/** Fields that say nothing to a reader of a diff: the ID is the row, and the stale mark is no part of the fact. */
const HIDDEN = new Set(['id', 'stale'])

function text(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  if (Array.isArray(value)) return value.length === 0 ? '—' : value.map(text).join(', ')
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

export interface FieldChange {
  field: string
  left: string
  right: string
}

/**
 * The fields that differ between two values of one fact, as text. A value that is missing
 * on one side gives every field of the other side. An enum can be a number in a stored
 * state and a word in a list; both are compared as they are written.
 */
export function changedFields(left: Value, right: Value): FieldChange[] {
  const fields = [...new Set([...Object.keys(left ?? {}), ...Object.keys(right ?? {})])].filter((f) => !HIDDEN.has(f))
  return fields
    .map((field) => ({ field, left: left ? text(left[field]) : '—', right: right ? text(right[field]) : '—' }))
    .filter((change) => change.left !== change.right)
}

/**
 * What to call a fact in a list of changes. `names` gives the name of an element or a
 * channel by its ID, for the facts that have no name of their own.
 */
export function entityLabel(entityType: string, value: Value, names: (id: string) => string | undefined, entityId: string): string {
  const of = (key: string) => {
    const id = value?.[key]
    return typeof id === 'string' ? names(id) ?? id.slice(0, 8) : '?'
  }
  const name = typeof value?.name === 'string' && value.name ? value.name : null
  if (!value) return names(entityId) ?? `${entityType} ${entityId.slice(0, 8)}`
  switch (entityType) {
    case 'relationship':
      return `${of('fromId')} → ${of('toId')}${typeof value.label === 'string' && value.label ? ` · ${value.label}` : ''}`
    case 'subscription':
      return `${of('consumerId')} on ${of('channelId')}`
    case 'binding':
      return `${of('sourceChannelId')} → ${of('targetChannelId')}${typeof value.routingKey === 'string' ? ` · ${value.routingKey}` : ''}`
    default:
      return name ?? `${entityType} ${entityId.slice(0, 8)}`
  }
}
