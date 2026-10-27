import { create } from 'zustand'
import { buildDuplicateSet, type DuplicateSet } from '@/lib/duplicate'

interface CopiedElement {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  ownerId: string | null
  parentId: string | null
  status: string
  tags: string[]
}

interface CopiedRelationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

/**
 * The cards a person copied, as they were at the copy: each card with what is inside it and the
 * relationships between them. A paste adds new elements from it, so a later change or delete of
 * the originals does not change what is pasted. It lives in this tab only.
 */
export interface Clip {
  rootIds: string[]
  elements: CopiedElement[]
  relationships: CopiedRelationship[]
  positions: { elementId: string; x: number; y: number }[]
}

interface ClipboardState {
  clip: Clip | null
  copy: (
    ids: string[],
    elements: CopiedElement[],
    relationships: CopiedRelationship[],
    positions: { elementId: string; x: number; y: number }[],
  ) => void
}

export const useClipboardStore = create<ClipboardState>((set) => ({
  clip: null,
  copy: (ids, elements, relationships, positions) => {
    const byId = new Map(elements.map((e) => [e.id, e]))
    // A card inside another copied card goes with that card.
    const inside = (id: string) => {
      for (let at = byId.get(id)?.parentId; at; at = byId.get(at)?.parentId) if (ids.includes(at)) return true
      return false
    }
    const rootIds = ids.filter((id) => byId.has(id) && !inside(id))
    const kept = new Set(rootIds)
    for (let grew = true; grew;) {
      grew = false
      for (const e of elements) if (e.parentId && kept.has(e.parentId) && !kept.has(e.id)) { kept.add(e.id); grew = true }
    }
    set({
      clip: {
        rootIds,
        elements: elements.filter((e) => kept.has(e.id)).map((e) => ({ ...e, tags: [...e.tags] })),
        relationships: relationships.filter((r) => kept.has(r.fromId) && kept.has(r.toId)),
        positions: positions.filter((p) => kept.has(p.elementId)).map(({ elementId, x, y }) => ({ elementId, x, y })),
      },
    })
  },
}))

/**
 * New elements from a clip, with new IDs: the copied cards go into `parentId` with their
 * top-left card at `at`, and keep their places to each other; what was inside a card keeps its
 * place inside the copy. A card whose kind cannot go into the parent is left out, with what is
 * inside it.
 */
export function pasteSet(clip: Clip, parentId: string | null, at: { x: number; y: number }, fits: (kind: string) => boolean): DuplicateSet {
  const roots = clip.rootIds.filter((id) => {
    const kind = clip.elements.find((e) => e.id === id)?.kind
    return kind !== undefined && fits(kind)
  })
  if (roots.length === 0) return { elements: [], relationships: [], positions: [] }
  const set = buildDuplicateSet(roots, clip.elements, clip.relationships, clip.positions)
  const rootPositions = roots.map((id) => clip.positions.find((p) => p.elementId === id) ?? { elementId: id, x: 0, y: 0 })
  const minX = Math.min(...rootPositions.map((p) => p.x))
  const minY = Math.min(...rootPositions.map((p) => p.y))
  const rootSet = new Set(roots)
  const positions: DuplicateSet['positions'] = []
  for (const copy of set.elements) {
    const saved = clip.positions.find((p) => p.elementId === copy.sourceId)
    if (rootSet.has(copy.sourceId)) positions.push({ elementId: copy.newId, x: at.x + (saved?.x ?? 0) - minX, y: at.y + (saved?.y ?? 0) - minY })
    // A card inside a copied card keeps its place inside; without a saved place it takes the default grid.
    else if (saved) positions.push({ elementId: copy.newId, x: saved.x, y: saved.y })
  }
  return {
    elements: set.elements.map((copy) => (rootSet.has(copy.sourceId) ? { ...copy, parentId } : copy)),
    relationships: set.relationships,
    positions,
  }
}
