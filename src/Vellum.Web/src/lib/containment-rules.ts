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
