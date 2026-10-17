/**
 * Type it: one line per fact, written to the model as `declared` facts.
 *
 *   Orders API                      an element (a system unless a kind is given)
 *   Customer [actor]                an element of a kind
 *   Orders DB [store] in Shop       an app or a store inside a system
 *   Web app -> Orders API           a relationship
 *   Web app -> Orders API: places orders    a relationship with a label
 *
 * Each end of a relationship may carry a kind and `in`, as an element line does. A name that
 * the model has already (case does not matter) is that element; nothing new is made for it.
 * An app or a store without `in` goes into the system named after the project, and a component
 * needs `in` with the name of its app. These follow the containment rules: only actors and
 * systems stand at the top level.
 */

export type ElementKind = 'actor' | 'system' | 'app' | 'store' | 'component'

export interface Endpoint {
  name: string
  kind?: ElementKind
  /** The name of the element that holds this one. */
  parent?: string
}

export type TypedLine =
  | { type: 'element'; element: Endpoint }
  | { type: 'relationship'; from: Endpoint; to: Endpoint; label?: string }
  | { type: 'error'; message: string }

const KIND_WORDS: Record<string, ElementKind> = {
  actor: 'actor', person: 'actor', user: 'actor',
  system: 'system', external: 'system',
  app: 'app', application: 'app', service: 'app', api: 'app', web: 'app', worker: 'app',
  store: 'store', db: 'store', database: 'store', queue: 'store', topic: 'store', cache: 'store', bucket: 'store',
  component: 'component', module: 'component',
}

const PARENT_KIND: Partial<Record<ElementKind, ElementKind>> = { app: 'system', store: 'system', component: 'app' }

const ENDPOINT = /^(.*?)\s*(?:\[\s*([^\]]*?)\s*\](?:\s+in\s+(.+))?)?$/i

function parseEndpoint(text: string): Endpoint | string {
  const match = ENDPOINT.exec(text.trim())
  const name = match?.[1]?.trim() ?? ''
  if (!match || name === '') return 'A name is missing.'
  const endpoint: Endpoint = { name }
  if (match[2] !== undefined) {
    const kind = KIND_WORDS[match[2].toLowerCase()]
    if (!kind) return `“${match[2]}” is no kind. Use actor, system, app, store or component.`
    endpoint.kind = kind
  }
  const parent = match[3]?.trim()
  if (parent) endpoint.parent = parent
  return endpoint
}

/** Reads one line. An empty line or a `#` comment gives null. */
export function parseTypedLine(line: string): TypedLine | null {
  const text = line.trim()
  if (text === '' || text.startsWith('#')) return null
  const arrow = /->|→/.exec(text)
  if (!arrow) {
    if (text.includes(':')) return { type: 'error', message: 'A label needs a relationship: write “A -> B: label”.' }
    const element = parseEndpoint(text)
    return typeof element === 'string' ? { type: 'error', message: element } : { type: 'element', element }
  }
  const left = text.slice(0, arrow.index)
  let right = text.slice(arrow.index + arrow[0].length)
  if (/->|→/.test(right)) return { type: 'error', message: 'One relationship per line: write “A -> B” and “B -> C” on two lines.' }
  let label: string | undefined
  const colon = right.indexOf(':')
  if (colon >= 0) {
    label = right.slice(colon + 1).trim() || undefined
    right = right.slice(0, colon)
  }
  const from = parseEndpoint(left)
  const to = parseEndpoint(right)
  if (typeof from === 'string') return { type: 'error', message: `Before the arrow: ${from}` }
  if (typeof to === 'string') return { type: 'error', message: `After the arrow: ${to}` }
  if (from.name.toLowerCase() === to.name.toLowerCase()) return { type: 'error', message: 'A relationship needs two different elements.' }
  return { type: 'relationship', from, to, label }
}

export interface KnownElement {
  id: string
  name: string
  kind: string
  parentId: string | null
}

export interface KnownRelationship {
  fromId: string
  toId: string
  label: string | null
}

export type ModelWrite =
  | { type: 'element'; id: string; name: string; kind: ElementKind; parentId: string | null }
  | { type: 'relationship'; id: string; fromId: string; toId: string; label: string | null }

export interface PlanOptions {
  /** The system that takes an app or a store without `in`: the name of the project. */
  defaultSystem: string
  newId: () => string
}

/**
 * The writes one line needs, in an order the server takes: a parent before its child, the
 * ends before the relationship. Nothing is written for a fact the model holds already.
 */
export function planTypedLine(
  line: Exclude<TypedLine, { type: 'error' }>,
  model: { elements: KnownElement[]; relationships: KnownRelationship[] },
  options: PlanOptions,
): { writes: ModelWrite[] } | { error: string } {
  const writes: ModelWrite[] = []
  const elements = [...model.elements]
  const byName = (name: string) => elements.find((e) => e.name.trim().toLowerCase() === name.trim().toLowerCase())

  const resolve = (endpoint: Endpoint): KnownElement | string => {
    const existing = byName(endpoint.name)
    if (existing) return existing
    const kind = endpoint.kind ?? 'system'
    let parentId: string | null = null
    const parentKind = PARENT_KIND[kind]
    if (parentKind) {
      const parentName = endpoint.parent ?? (kind === 'component' ? null : options.defaultSystem)
      if (!parentName) return `A component sits in an app: write “${endpoint.name} [component] in <app>”.`
      const parent = resolve({ name: parentName, kind: parentKind })
      if (typeof parent === 'string') return parent
      if (parent.kind !== parentKind) return `${parent.name} is ${article(parent.kind)} ${parent.kind}. ${article(kind, true)} ${kind} sits in ${article(parentKind)} ${parentKind}.`
      parentId = parent.id
    } else if (endpoint.parent) {
      return `${article(kind, true)} ${kind} stands at the top level. Leave out “in ${endpoint.parent}”.`
    }
    const created: KnownElement = { id: options.newId(), name: endpoint.name, kind, parentId }
    elements.push(created)
    writes.push({ type: 'element', id: created.id, name: created.name, kind, parentId })
    return created
  }

  if (line.type === 'element') {
    const element = resolve(line.element)
    return typeof element === 'string' ? { error: element } : { writes }
  }
  const from = resolve(line.from)
  if (typeof from === 'string') return { error: from }
  const to = resolve(line.to)
  if (typeof to === 'string') return { error: to }
  const label = line.label ?? null
  const known = model.relationships.some((r) => r.fromId === from.id && r.toId === to.id && (r.label ?? null) === label)
  if (!known) writes.push({ type: 'relationship', id: options.newId(), fromId: from.id, toId: to.id, label })
  return { writes }
}

function article(kind: string, capital = false): string {
  const word = /^[aeiou]/.test(kind) ? 'an' : 'a'
  return capital ? word[0].toUpperCase() + word.slice(1) : word
}
