import type { ChangeImpact, ImpactCertainty, ImpactFlow, ProposalEvidence } from '@/lib/import-api'

/**
 * The words, colours and line styles of impact certainty. The impact report, the canvas and
 * the trace dock use the same three, so a step reads the same wherever it shows. The line
 * style repeats what the colour says: solid, dashed, dotted.
 */
export const CERTAINTY: Record<ImpactCertainty, { label: string; meaning: string; color: string; line: 'solid' | 'dashed' | 'dotted'; dash: string | undefined; className: string }> = {
  definite: {
    label: 'Definite', meaning: 'It stands on a contract diff or on source lines that this report cites.',
    color: 'var(--foreground)', line: 'solid', dash: undefined, className: 'border-foreground/70 text-foreground',
  },
  possible: {
    label: 'Possible', meaning: 'It is reached only through a flow or a service. Nothing shows that it depends on the change.',
    color: 'var(--trace-retry)', line: 'dashed', dash: '8 5', className: 'border-dashed border-[var(--trace-retry)] text-[var(--trace-retry)]',
  },
  unknown: {
    label: 'Unknown', meaning: 'The scan did not look where the answer is.',
    color: 'var(--muted-foreground)', line: 'dotted', dash: '2 5', className: 'border-dotted border-muted-foreground text-muted-foreground',
  },
}

const RANK: Record<ImpactCertainty, number> = { definite: 2, possible: 1, unknown: 0 }

/** The surest of the values, as the report ranks them. */
export function strongest(values: ImpactCertainty[]): ImpactCertainty {
  return values.reduce<ImpactCertainty>((best, value) => (RANK[value] > RANK[best] ? value : best), 'unknown')
}

/** One reason of a step with the change it stands on, so the step links to that change's evidence. */
export interface StepReason {
  certainty: ImpactCertainty
  text: string
  change: { id: string; name: string; evidence: ProposalEvidence[] } | null
}

/** What the impact says of one step of a flow. */
export interface StepImpact {
  stepId: string
  label: string | null
  certainty: ImpactCertainty
  reasons: StepReason[]
}

/** The affected steps of a flow by step ID, each with the changes and evidence it stands on. */
export function flowStepImpact(impact: ChangeImpact, flow: ImpactFlow): Record<string, StepImpact> {
  const changes = new Map(impact.changes.map((c) => [c.id, c]))
  return Object.fromEntries(flow.steps.map((step) => [step.stepId, {
    stepId: step.stepId,
    label: step.label,
    certainty: step.certainty,
    reasons: step.reasons.map((reason) => {
      const change = changes.get(reason.changeId)
      const name = change ? `${change.kind === 'source-pattern' ? 'pattern' : change.entityKind} ${change.name}` : ''
      return { certainty: reason.certainty, text: reason.text, change: change ? { id: change.id, name, evidence: change.evidence } : null }
    }),
  }]))
}
