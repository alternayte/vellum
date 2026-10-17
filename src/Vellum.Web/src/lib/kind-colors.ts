const KIND_COLORS: Record<string, string> = {
  actor: 'var(--kind-actor)',
  system: 'var(--kind-system)',
  app: 'var(--kind-app)',
  store: 'var(--kind-store)',
  component: 'var(--kind-component)',
}

export function kindColor(kind: string): string {
  return KIND_COLORS[kind.toLowerCase()] ?? 'var(--muted-foreground)'
}

/** The tag of a system outside the architecture's own code (Structurizr's External). */
export const EXTERNAL_TAG = 'external'

export function isExternal(tags: readonly string[] | null | undefined): boolean {
  return !!tags?.some((tag) => tag.toLowerCase() === EXTERNAL_TAG)
}

/** The colour of an element: the colour of its kind, muted for an external system. */
export function elementColor(kind: string, tags: readonly string[] | null | undefined): string {
  return isExternal(tags) ? 'var(--muted-foreground)' : kindColor(kind)
}
