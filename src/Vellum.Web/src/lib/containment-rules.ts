export const CONTAINMENT_RULES: Record<string, string[]> = {
  actor: [],
  system: ['app', 'store'],
  app: ['component'],
  store: [],
  component: [],
}

export function validKindsForContext(parentKind: string | null): string[] {
  if (parentKind === null) return ['actor', 'system']
  return CONTAINMENT_RULES[parentKind] ?? []
}

/** True when an element of this kind can hold other elements. */
export function canContain(kind: string): boolean {
  return (CONTAINMENT_RULES[kind]?.length ?? 0) > 0
}

/** The element and its ancestors, from the top level down. Empty when the ID is unknown. */
export function pathTo<T extends { id: string; parentId: string | null }>(elements: T[], id: string): T[] {
  const byId = new Map(elements.map((element) => [element.id, element]))
  const path: T[] = []
  for (let current = byId.get(id); current && !path.includes(current); current = current.parentId ? byId.get(current.parentId) : undefined) {
    path.unshift(current)
  }
  return path
}
