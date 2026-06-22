const STRATA: Record<string, string> = {
  actor: 'Context',
  system: 'Context',
  app: 'Container',
  store: 'Container',
  component: 'Component',
}

export function stratum(kind: string): string {
  return STRATA[kind.toLowerCase()] ?? 'Context'
}

export function stratumLabel(kind: string, name: string): string {
  const s = stratum(kind)
  if (kind === 'actor' || kind === 'system') return name
  return `${s}: ${name}`
}
