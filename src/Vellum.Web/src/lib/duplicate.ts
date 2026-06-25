interface Element {
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

interface Relationship {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

interface LayoutPosition {
  elementId: string
  x: number
  y: number
}

export interface DuplicateElement {
  newId: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  parentId: string | null
  status: string
  tags: string[]
}

export interface DuplicateRelationship {
  newId: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

export interface DuplicatePosition {
  elementId: string
  x: number
  y: number
}

export interface DuplicateSet {
  elements: DuplicateElement[]
  relationships: DuplicateRelationship[]
  positions: DuplicatePosition[]
}

const OFFSET = 40

export function buildDuplicateSet(
  selectedIds: string[],
  allElements: Element[],
  allRelationships: Relationship[],
  allPositions: LayoutPosition[],
): DuplicateSet {
  const selected = new Set(selectedIds)

  // Expand selection to include children of selected containers
  const expandedIds = new Set(selected)
  const queue = [...selectedIds]
  while (queue.length > 0) {
    const id = queue.pop()!
    for (const el of allElements) {
      if (el.parentId === id && !expandedIds.has(el.id)) {
        expandedIds.add(el.id)
        queue.push(el.id)
      }
    }
  }

  // Build old→new ID mapping
  const idMap = new Map<string, string>()
  for (const id of expandedIds) {
    idMap.set(id, crypto.randomUUID())
  }

  const posMap = new Map<string, LayoutPosition>()
  for (const p of allPositions) posMap.set(p.elementId, p)

  const elements: DuplicateElement[] = []
  for (const el of allElements) {
    if (!expandedIds.has(el.id)) continue
    const newParentId = el.parentId && idMap.has(el.parentId)
      ? idMap.get(el.parentId)!
      : el.parentId
    elements.push({
      newId: idMap.get(el.id)!,
      kind: el.kind,
      name: `${el.name} (copy)`,
      description: el.description,
      technology: el.technology,
      parentId: newParentId,
      status: el.status,
      tags: [...el.tags],
    })
  }

  const relationships: DuplicateRelationship[] = []
  for (const rel of allRelationships) {
    if (expandedIds.has(rel.fromId) && expandedIds.has(rel.toId)) {
      relationships.push({
        newId: crypto.randomUUID(),
        fromId: idMap.get(rel.fromId)!,
        toId: idMap.get(rel.toId)!,
        label: rel.label,
        technology: rel.technology,
      })
    }
  }

  const positions: DuplicatePosition[] = []
  for (const el of elements) {
    const origId = [...idMap.entries()].find(([, v]) => v === el.newId)?.[0]
    const origPos = origId ? posMap.get(origId) : undefined
    positions.push({
      elementId: el.newId,
      x: (origPos?.x ?? 0) + OFFSET,
      y: (origPos?.y ?? 0) + OFFSET,
    })
  }

  return { elements, relationships, positions }
}
