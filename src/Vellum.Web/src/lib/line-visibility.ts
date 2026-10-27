/**
 * Line visibility: which lines a view draws. It is layout data of the view, never a model fact,
 * and the same rules apply in the exports and on a share link (LineVisibility.cs on the server):
 * - A relationship hidden one at a time, or of a kind that is turned off, is not drawn in this
 *   view at all: neither as itself nor inside a derived line.
 * - A cross-level line is a direct relationship whose two visible ends sit at different nesting
 *   depths. With `crossLevel` off, those are not drawn.
 * - With `derivedLines` off, no derived line is drawn. A derived line hidden one at a time is
 *   keyed by its pair of cards: a line between other cards shows, and the line stays hidden while
 *   the set of relationships under it changes.
 */
import { projectLines, type DerivedLine, type HiddenLine, type LineElement, type LineRelationship } from './derived-lines'

export interface HiddenDerivedLine {
  fromId: string
  toId: string
}

export interface LineVisibility {
  derivedLines: boolean
  structural: boolean
  behavioural: boolean
  crossLevel: boolean
  hiddenRelationshipIds: string[]
  hiddenDerivedLines: HiddenDerivedLine[]
}

export const ALL_LINES_SHOWN: LineVisibility = {
  derivedLines: true,
  structural: true,
  behavioural: true,
  crossLevel: true,
  hiddenRelationshipIds: [],
  hiddenDerivedLines: [],
}

/** The four switches of a view, in the order the controls show them. */
export const LINE_SWITCHES = [
  { key: 'derivedLines', label: 'Derived lines' },
  { key: 'structural', label: 'Structural lines' },
  { key: 'behavioural', label: 'Behavioural lines' },
  { key: 'crossLevel', label: 'Cross-level lines' },
] as const satisfies readonly { key: keyof LineVisibility; label: string }[]

export type LineSwitch = (typeof LINE_SWITCHES)[number]['key']

/** A saved visibility as the client uses it; a missing value shows every line. */
export function lineVisibilityOf(saved: Partial<LineVisibility> | null | undefined): LineVisibility {
  if (!saved) return ALL_LINES_SHOWN
  return {
    derivedLines: saved.derivedLines ?? true,
    structural: saved.structural ?? true,
    behavioural: saved.behavioural ?? true,
    crossLevel: saved.crossLevel ?? true,
    hiddenRelationshipIds: saved.hiddenRelationshipIds ?? [],
    hiddenDerivedLines: saved.hiddenDerivedLines ?? [],
  }
}

/** True when the view hides at least one line or kind of line. */
export function hidesAnything(v: LineVisibility): boolean {
  return !v.derivedLines || !v.structural || !v.behavioural || !v.crossLevel || v.hiddenRelationshipIds.length > 0 || v.hiddenDerivedLines.length > 0
}

/** True when the view draws this relationship: it is not hidden by ID or by kind. */
export function drawsRelationship(v: LineVisibility, r: { id: string; kind?: string | null }): boolean {
  if (v.hiddenRelationshipIds.includes(r.id)) return false
  return r.kind === 'behavioural' ? v.behavioural : v.structural
}

/** How many ancestors an element has. A cycle of parents stops the count. */
export function nestingDepth(id: string, parentOf: ReadonlyMap<string, string | null>): number {
  let depth = 0
  const seen = new Set([id])
  for (let at = parentOf.get(id); at != null && parentOf.has(at) && !seen.has(at); at = parentOf.get(at)) {
    seen.add(at)
    depth++
  }
  return depth
}

/** True for a direct relationship whose two ends sit at different nesting depths. */
export function isCrossLevel(r: LineRelationship, parentOf: ReadonlyMap<string, string | null>): boolean {
  return nestingDepth(r.fromId, parentOf) !== nestingDepth(r.toId, parentOf)
}

export interface VisibleLines<R extends LineRelationship> {
  /** The relationships drawn as themselves. */
  direct: R[]
  /** The derived lines drawn. */
  derived: DerivedLine[]
  /** The relationships that would be drawn as themselves if the view hid nothing, and are not. */
  hiddenDirect: R[]
  /** The derived lines that would be drawn if the view hid nothing, and are not. */
  hiddenDerived: DerivedLine[]
}

/**
 * The lines a view draws under its visibility: a relationship hidden by ID or by kind is left out
 * before the lines are projected, then a cross-level direct line and a hidden derived line are
 * left out. The hidden lists hold what the view would draw without its visibility, so the canvas
 * can count them and draw them faint when a person asks to see them.
 */
export function applyLineVisibility<R extends LineRelationship & { kind?: string | null }>(
  elements: readonly LineElement[],
  relationships: readonly R[],
  visible: ReadonlySet<string>,
  visibility: LineVisibility,
  shareHidden: readonly HiddenLine[] = [],
): VisibleLines<R> {
  const all = projectLines(elements, relationships, visible, shareHidden)
  if (!hidesAnything(visibility)) return { ...all, hiddenDirect: [], hiddenDerived: [] }

  const parentOf = new Map(elements.map((e) => [e.id, e.parentId]))
  const kept = projectLines(elements, relationships.filter((r) => drawsRelationship(visibility, r)), visible, shareHidden)
  const hiddenPairs = new Set(visibility.hiddenDerivedLines.map((l) => `${l.fromId}>${l.toId}`))
  const direct = visibility.crossLevel ? kept.direct : kept.direct.filter((r) => !isCrossLevel(r, parentOf))
  const derived = visibility.derivedLines ? kept.derived.filter((l) => !hiddenPairs.has(`${l.fromId}>${l.toId}`)) : []

  const directIds = new Set(direct.map((r) => r.id))
  const derivedIds = new Set(derived.map((l) => l.id))
  return {
    direct,
    derived,
    hiddenDirect: all.direct.filter((r) => !directIds.has(r.id)),
    hiddenDerived: all.derived.filter((l) => !derivedIds.has(l.id)),
  }
}

/** The visibility with one switch set. */
export function withSwitch(v: LineVisibility, key: LineSwitch, shown: boolean): LineVisibility {
  return { ...v, [key]: shown }
}

/** The visibility with one relationship hidden or shown again. */
export function withRelationship(v: LineVisibility, id: string, hidden: boolean): LineVisibility {
  const rest = v.hiddenRelationshipIds.filter((r) => r !== id)
  return { ...v, hiddenRelationshipIds: hidden ? [...rest, id] : rest }
}

/** The visibility with one derived line hidden or shown again, by its two cards. */
export function withDerivedLine(v: LineVisibility, pair: HiddenDerivedLine, hidden: boolean): LineVisibility {
  const rest = v.hiddenDerivedLines.filter((l) => l.fromId !== pair.fromId || l.toId !== pair.toId)
  return { ...v, hiddenDerivedLines: hidden ? [...rest, { fromId: pair.fromId, toId: pair.toId }] : rest }
}
