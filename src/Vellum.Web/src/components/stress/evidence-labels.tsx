import { CircleHelp, Ruler } from 'lucide-react'
import type { EvidenceLabel } from '@/lib/load-tests-api'

/**
 * The three labels a number can carry where estimates and measurements meet. Each has its
 * own shape, fill and colour, so a measured number never reads as an estimate:
 * an estimate is an amber outline pill, a measurement is a filled teal tag with a ruler,
 * and a missing value is a dashed grey tag with a question mark.
 */

export function EstimatedLabel() {
  return (
    <span className="rounded-full border border-[var(--trace-retry)] px-1.5 py-px text-[10px] font-medium uppercase tracking-wider text-[var(--trace-retry)]">
      Estimated
    </span>
  )
}

export function MeasuredLabel() {
  return (
    <span className="inline-flex items-center gap-1 rounded-sm bg-[var(--trace-request)] px-1.5 py-px text-[10px] font-semibold uppercase tracking-wider text-[hsl(200_30%_8%)]">
      <Ruler aria-hidden className="h-2.5 w-2.5" />
      Measured
    </span>
  )
}

export function UnknownLabel() {
  return (
    <span className="inline-flex items-center gap-1 rounded-sm border border-dashed border-muted-foreground/70 px-1.5 py-px text-[10px] font-medium uppercase tracking-wider text-muted-foreground">
      <CircleHelp aria-hidden className="h-2.5 w-2.5" />
      Unknown
    </span>
  )
}

export function EvidenceTag({ label }: { label: EvidenceLabel }) {
  return label === 'measured' ? <MeasuredLabel /> : label === 'estimated' ? <EstimatedLabel /> : <UnknownLabel />
}

/** The cell style of a value column. The column of estimates is amber with a dashed rule; the column of measurements is teal with a solid rule. */
export const estimatedCell = 'border-l border-dashed border-[var(--trace-retry)]/60 text-[var(--trace-retry)]'
export const measuredCell = 'border-l-2 border-solid border-[var(--trace-request)] font-semibold text-foreground'
