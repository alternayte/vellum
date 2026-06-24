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
