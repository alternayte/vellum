export interface Provenance {
  source: string | null
  revision: string | null
  confidence: number | null
  acceptedBy: string | null
  acceptedAt: string | null
}

/**
 * Truth labels from the domain model. `declared` is the default and stays visually quiet;
 * every other label gets a marker so an inference is never mistaken for a fact.
 */
export const FACT_STATUS_COLORS: Record<string, string> = {
  declared: 'var(--muted-foreground)',
  'observed-in-source': '#2dd4bf',
  inferred: '#f59e0b',
  measured: '#22c55e',
  unknown: '#f87171',
}

export function factStatusColor(status: string): string {
  return FACT_STATUS_COLORS[status] ?? FACT_STATUS_COLORS.unknown
}

export function factStatusLabel(status: string): string {
  switch (status) {
    case 'observed-in-source':
      return 'Observed in source'
    case 'inferred':
      return 'Inferred'
    case 'measured':
      return 'Measured'
    case 'unknown':
      return 'Unknown'
    default:
      return 'Declared'
  }
}

export function isUnverified(status: string | null | undefined): boolean {
  return status === 'inferred' || status === 'unknown'
}

export function provenanceSummary(provenance: Provenance | null | undefined): string | null {
  if (!provenance) return null

  const parts: string[] = []
  if (provenance.source) parts.push(provenance.source)
  if (provenance.revision) parts.push(provenance.revision)
  if (provenance.confidence != null) parts.push(`${Math.round(provenance.confidence * 100)}% confidence`)
  if (provenance.acceptedAt) parts.push(`accepted ${new Date(provenance.acceptedAt).toLocaleDateString()}`)

  return parts.length > 0 ? parts.join(' · ') : null
}

/** Set when a complete rescan no longer finds the source evidence of an accepted fact. */
export interface StaleMark {
  sinceRevision: string
  reason: string
}

export function staleSummary(stale: StaleMark): string {
  return `Stale: the last complete scan (${stale.sinceRevision.slice(0, 10)}) did not find this in the source`
}
