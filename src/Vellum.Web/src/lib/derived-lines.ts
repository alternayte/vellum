/**
 * Derived lines: the lines a view draws between two visible cards for the relationships whose
 * ends are hidden inside them. A derived line is a projection of the view; it is never stored.
 *
 * The rules, the same as DerivedLines.Project on the server (the exports draw by it):
 * - A relationship whose two ends are visible is direct: it draws as itself.
 * - Otherwise each end attaches to its nearest visible ancestor. An end with none is not in
 *   the view, and the relationship draws nothing.
 * - Two ends that attach to the same card draw nothing.
 * - The relationships between the same two visible cards, in one direction, merge into one
 *   derived line. Its relationships are in ordinal order of ID, and the lines are in ordinal
 *   order of the source card, then of the target card.
 * - The label of a line with one relationship is that relationship's label; a line with more
 *   says "N relationships".
 */

export interface LineElement {
  id: string
  parentId: string | null
}

export interface LineRelationship {
  id: string
  fromId: string
  toId: string
  label?: string | null
}

export interface DerivedLine {
  /** The canvas ID of the line: never the ID of a fact. */
  id: string
  fromId: string
  toId: string
  relationshipIds: string[]
  label: string | null
}

const DERIVED_PREFIX = 'derived:'
export const derivedLineId = (fromId: string, toId: string) => `${DERIVED_PREFIX}${fromId}>${toId}`
export const isDerivedLineId = (id: string) => id.startsWith(DERIVED_PREFIX)

const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0)

/** The label of a derived line with these relationships. */
export function derivedLineLabel(relationships: { label?: string | null }[]): string | null {
  return relationships.length === 1 ? (relationships[0].label ?? null) : `${relationships.length} relationships`
}

/** Splits the relationships of a view into those drawn directly and the derived lines of the rest. */
export function projectLines<R extends LineRelationship>(
  elements: readonly LineElement[],
  relationships: readonly R[],
  visible: ReadonlySet<string>,
): { direct: R[]; derived: DerivedLine[] } {
  const parentOf = new Map(elements.map((e) => [e.id, e.parentId]))
  const resolved = new Map<string, string | null>()
  const nearestVisible = (id: string): string | null => {
    if (resolved.has(id)) return resolved.get(id)!
    let found: string | null = null
    // The walk stops at a card it saw before, so a cycle of parents cannot hold it.
    const seen = new Set<string>()
    for (let at: string | null | undefined = id; at != null && parentOf.has(at) && !seen.has(at); at = parentOf.get(at)) {
      if (visible.has(at)) {
        found = at
        break
      }
      seen.add(at)
    }
    resolved.set(id, found)
    return found
  }

  const direct: R[] = []
  const groups = new Map<string, { fromId: string; toId: string; relationships: R[] }>()
  for (const r of relationships) {
    if (visible.has(r.fromId) && visible.has(r.toId)) {
      direct.push(r)
      continue
    }
    const fromId = nearestVisible(r.fromId)
    const toId = nearestVisible(r.toId)
    if (fromId === null || toId === null || fromId === toId) continue
    const key = derivedLineId(fromId, toId)
    const group = groups.get(key) ?? { fromId, toId, relationships: [] }
    group.relationships.push(r)
    groups.set(key, group)
  }

  const derived = [...groups.values()]
    .sort((a, b) => ordinal(a.fromId, b.fromId) || ordinal(a.toId, b.toId))
    .map(({ fromId, toId, relationships: rels }) => {
      const sorted = [...rels].sort((a, b) => ordinal(a.id, b.id))
      return {
        id: derivedLineId(fromId, toId),
        fromId,
        toId,
        relationshipIds: sorted.map((r) => r.id),
        label: derivedLineLabel(sorted),
      }
    })
  return { direct, derived }
}
