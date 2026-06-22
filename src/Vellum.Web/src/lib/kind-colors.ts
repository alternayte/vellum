const KIND_COLORS: Record<string, string> = {
  actor: 'hsl(var(--kind-actor))',
  system: 'hsl(var(--kind-system))',
  app: 'hsl(var(--kind-app))',
  store: 'hsl(var(--kind-store))',
  component: 'hsl(var(--kind-component))',
}

export function kindColor(kind: string): string {
  return KIND_COLORS[kind.toLowerCase()] ?? 'hsl(var(--muted-foreground))'
}
