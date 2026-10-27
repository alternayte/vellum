import { CONTAINMENT_RULES, validKindsForContext } from '@/lib/containment-rules'
import { EXTERNAL_TAG } from '@/lib/kind-colors'

/** One entry of the Create tab: a kind of element, or an external system (a system with the tag `external`). */
export interface PaletteEntry {
  id: string
  kind: string
  name: string
  description: string
  tags: string[]
}

export const PALETTE: PaletteEntry[] = [
  { id: 'actor', kind: 'actor', name: 'Actor', description: 'A person or a role that uses the system, such as a customer.', tags: [] },
  { id: 'system', kind: 'system', name: 'System', description: 'Software your team owns and runs, such as a shop.', tags: [] },
  { id: 'external', kind: 'system', name: 'External system', description: 'A service you do not own, such as a payment provider.', tags: [EXTERNAL_TAG] },
  { id: 'app', kind: 'app', name: 'App', description: 'A program that runs on its own inside a system, such as an API or a worker.', tags: [] },
  { id: 'store', kind: 'store', name: 'Store', description: 'A place where a system keeps data, such as a database, a cache or a queue.', tags: [] },
  { id: 'component', kind: 'component', name: 'Component', description: 'A part of the code of an app, such as a module or a controller.', tags: [] },
]

/** The type of the data that a dragged palette entry carries: the entry's ID. */
export const PALETTE_DRAG_TYPE = 'application/x-vellum-palette'

/** A card under the pointer, the innermost first. */
export interface CardUnderPointer {
  id: string
  kind: string
  external: boolean
}

export type DropPlace = { parentId: string | null; cardId: string | null } | { refused: string }

const article = (word: string) => (/^[aeiou]/i.test(word) ? 'an' : 'a')
const capital = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)

/** The kinds that can hold an element of this kind; none means the top level. */
const holdersOf = (kind: string) => Object.entries(CONTAINMENT_RULES).filter(([, kinds]) => kinds.includes(kind)).map(([holder]) => holder)

/**
 * Where a dropped element goes: inside the innermost card under the pointer that can hold its
 * kind, otherwise on the level the canvas shows when that level can hold it. Otherwise the drop
 * is refused with the rule. An external system offers no inside.
 */
export function dropPlace(kind: string, cards: CardUnderPointer[], level: { id: string; kind: string } | null): DropPlace {
  const host = cards.find((card) => !card.external && (CONTAINMENT_RULES[card.kind] ?? []).includes(kind))
  if (host) return { parentId: host.id, cardId: host.id }
  if (validKindsForContext(level?.kind ?? null).includes(kind)) return { parentId: level?.id ?? null, cardId: null }
  const holders = holdersOf(kind).map((holder) => `${article(holder)} ${holder}`)
  return {
    refused: holders.length === 0
      ? `${capital(article(kind))} ${kind} goes on the top level. Go back to the top level to add one.`
      : `${capital(article(kind))} ${kind} goes inside ${holders.join(' or ')}. Drop it on ${holders.join(' or ')} card.`,
  }
}
