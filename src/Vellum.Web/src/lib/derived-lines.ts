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
 *
 * A view share link does not share the parts hidden inside its cards. It sends their
 * relationships as hidden lines instead: a pair of shared cards, a count and, for one, a label.
 * A hidden line attaches to the nearest visible card of each of its two cards by the same rules
 * and adds its count to the derived line there. It never draws as a direct line: its
 * relationships run between parts inside the cards.
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

/** Relationships a share link does not share, between the hidden parts of two shared cards. */
export interface HiddenLine {
  fromId: string
  toId: string
  /** The label of the one relationship; null for more. */
  label: string | null
  count: number
}

export interface DerivedLine {
  /** The canvas ID of the line: never the ID of a fact. */
  id: string
  fromId: string
  toId: string
  relationshipIds: string[]
  /** How many relationships of the line the page does not share. */
  hiddenCount: number
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
  hidden: readonly HiddenLine[] = [],
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
  const groups = new Map<string, { fromId: string; toId: string; relationships: R[]; hidden: HiddenLine[] }>()
  const groupOf = (fromId: string, toId: string) => {
    const key = derivedLineId(fromId, toId)
    const group = groups.get(key) ?? { fromId, toId, relationships: [], hidden: [] }
    groups.set(key, group)
    return group
  }
  for (const r of relationships) {
    if (visible.has(r.fromId) && visible.has(r.toId)) {
      direct.push(r)
      continue
    }
    const fromId = nearestVisible(r.fromId)
    const toId = nearestVisible(r.toId)
    if (fromId === null || toId === null || fromId === toId) continue
    groupOf(fromId, toId).relationships.push(r)
  }
  for (const h of hidden) {
    if (h.count < 1) continue
    const fromId = nearestVisible(h.fromId)
    const toId = nearestVisible(h.toId)
    if (fromId === null || toId === null || fromId === toId) continue
    groupOf(fromId, toId).hidden.push(h)
  }

  const derived = [...groups.values()]
    .sort((a, b) => ordinal(a.fromId, b.fromId) || ordinal(a.toId, b.toId))
    .map(({ fromId, toId, relationships: rels, hidden: hiddenLines }) => {
      const sorted = [...rels].sort((a, b) => ordinal(a.id, b.id))
      const hiddenCount = hiddenLines.reduce((n, h) => n + h.count, 0)
      // One relationship in all: its label, shared or hidden. Otherwise "N relationships".
      const label = hiddenCount === 0
        ? derivedLineLabel(sorted)
        : sorted.length === 0 && hiddenCount === 1
          ? hiddenLines[0].label
          : `${sorted.length + hiddenCount} relationships`
      return {
        id: derivedLineId(fromId, toId),
        fromId,
        toId,
        relationshipIds: sorted.map((r) => r.id),
        hiddenCount,
        label,
      }
    })
  return { direct, derived }
}
