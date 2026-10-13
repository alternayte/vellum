import { api } from '@/lib/settings-api'
import type { Unavailable } from '@/lib/design-api'

export type RuleResult = 'verified' | 'concern' | 'unknown' | 'not-applicable'
export type Severity = 'blocking' | 'warning' | 'note'
export type Category = 'resilience' | 'contracts' | 'truth' | 'overengineering'
export type DispositionStatus = 'open' | 'accepted-by-decision' | 'wont-fix'

/** A part of the model that a finding or a suggestion names, by its stable ID. */
export interface Cited {
  kind: string
  id: string
  name: string
}

export interface Finding {
  ruleId: string
  ruleVersion: number
  category: Category
  severity: Severity
  result: RuleResult
  subject: Cited
  cites: Cited[]
  reason: string
  wouldChange: string
  /** The question an overengineering rule asks. A decision answers it. */
  question: string | null
  fingerprint: string
  /** True for a concern or an unknown: a result that asks for attention. */
  fired: boolean
}

/** A finding with what a person did about it. */
export interface FindingView {
  finding: Finding
  status: DispositionStatus
  decisionId: string | null
  /** The number of the decision, for example ADR-0003. */
  decision: string | null
  /** The reason of a wont-fix. */
  reason: string | null
  /** The finding changed after a person answered it. The answer settles nothing until it is reviewed again. */
  needsReReview: boolean
  settled: boolean
  /** The finding prevents Build Ready. */
  blocks: boolean
}

export interface ReviewCounts {
  blocking: number
  concerns: number
  unknown: number
  verified: number
  notApplicable: number
  settled: number
  needsReReview: number
}

export interface FindingsResponse {
  projectId: string
  branch: string
  modelVersion: number
  revisionId: string | null
  inputsFingerprint: string
  ruleSetVersion: string
  hash: string
  counts: ReviewCounts
  findings: FindingView[]
}

export interface Rule {
  id: string
  version: number
  category: Category
  title: string
  reads: string
  settle: string
}

export interface SnapshotSummary {
  id: string
  branch: string
  modelVersion: number
  revisionId: string | null
  inputsFingerprint: string
  ruleSetVersion: string
  hash: string
  note: string | null
  createdBy: string
  createdAt: string
}

export interface Alternative {
  title: string
  whyNot: string | null
}

export interface Settles {
  ruleId: string
  subjectId: string
}

export type DecisionStatus = 'proposed' | 'accepted' | 'superseded' | 'rejected'

export interface Decision {
  id: string
  /** ADR-0001. Issued once in the project. */
  number: string
  title: string
  status: DecisionStatus
  origin: string
  context: string
  decision: string
  alternatives: Alternative[]
  consequences: string | null
  affectedIds: string[]
  settles: Settles[]
  supersedes: string | null
  supersededBy: string | null
  createdBy: string
  createdAt: string
  acceptedBy: string | null
  acceptedAt: string | null
  history: { at: string; by: string; action: string; note: string | null }[]
}

export interface DecisionInput {
  title: string
  context: string
  decision: string
  alternatives: Alternative[]
  consequences: string | null
  affectedIds: string[]
  settles: Settles[]
  supersedes: string | null
}

export interface Question {
  id: string
  text: string
  about: string[]
  origin: string
  status: 'open' | 'answered' | 'dropped'
  answer: string | null
  answeredBy: string | null
  answeredAt: string | null
  createdAt: string
}

export interface CritiqueQuestion {
  about: Cited[]
  text: string
  why: string | null
  /** `unsupported-risk`: a risk the assistant claimed with no finding behind it. */
  source: 'assistant' | 'unsupported-risk'
  note: string | null
  keptAs: string | null
}

export interface CritiqueRisk {
  about: Cited[]
  claim: string
  ruleId: string
  subjectId: string
}

export interface CritiqueProposedDecision {
  title: string
  context: string
  decision: string
  alternatives: Alternative[]
  settles: Settles[]
  about: Cited[]
  keptAs: string | null
}

export interface Critique {
  id: string
  branchId: string | null
  question: string
  questions: CritiqueQuestion[]
  risks: CritiqueRisk[]
  proposedDecisions: CritiqueProposedDecision[]
  leftOut: { where: string; reason: string }[]
  modelVersion: number
  /** The model changed after the answer was made. */
  stale: boolean
  findingsSent: number
  findingsLeftOut: number
  omittedPrivate: { count: number; reason: string } | null
  provider: { kind: string; model: string; reportedModel: string | null }
  createdAt: string
}

const base = (projectId: string) => `/api/projects/${projectId}`
const branchQuery = (branchId?: string | null) => (branchId ? `?branchId=${branchId}` : '')

export const reviewApi = {
  rules: (projectId: string) => api<Rule[]>('GET', `${base(projectId)}/review/rules`),
  findings: (projectId: string, branchId?: string | null) => api<FindingsResponse>('GET', `${base(projectId)}/review/findings${branchQuery(branchId)}`),
  setDisposition: (
    projectId: string, branchId: string | null | undefined, finding: Finding, status: DispositionStatus, detail: { decisionId?: string; reason?: string } = {},
  ) =>
    api<FindingView>('PUT', `${base(projectId)}/review/dispositions`, {
      ruleId: finding.ruleId, subjectId: finding.subject.id, status,
      decisionId: detail.decisionId ?? null, reason: detail.reason ?? null, revisionId: null, branchId: branchId ?? null,
    }),
  snapshots: async (projectId: string) => (await api<{ items: SnapshotSummary[] }>('GET', `${base(projectId)}/review/snapshots`)).items,
  saveSnapshot: (projectId: string, branchId: string | null | undefined, note: string | null) =>
    api<SnapshotSummary>('POST', `${base(projectId)}/review/snapshots`, { revisionId: null, branchId: branchId ?? null, note }),
  deleteSnapshot: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/review/snapshots/${id}`),

  decisions: async (projectId: string) => (await api<{ items: Decision[] }>('GET', `${base(projectId)}/decisions`)).items,
  createDecision: (projectId: string, input: DecisionInput) => api<Decision>('POST', `${base(projectId)}/decisions`, { ...input, origin: 'human' }),
  updateDecision: (projectId: string, id: string, input: DecisionInput) => api<Decision>('PUT', `${base(projectId)}/decisions/${id}`, input),
  acceptDecision: (projectId: string, id: string) => api<Decision>('POST', `${base(projectId)}/decisions/${id}/accept`),
  rejectDecision: (projectId: string, id: string, reason: string | null) => api<Decision>('POST', `${base(projectId)}/decisions/${id}/reject`, { reason }),
  deleteDecision: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/decisions/${id}`),
  decisionExportUrl: (projectId: string, id?: string) => (id ? `${base(projectId)}/decisions/${id}/export` : `${base(projectId)}/decisions/export`),

  questions: async (projectId: string) => (await api<{ items: Question[] }>('GET', `${base(projectId)}/questions`)).items,
  addQuestion: (projectId: string, text: string) => api<Question>('POST', `${base(projectId)}/questions`, { text, about: [], origin: 'human' }),
  /** Keeps one open question of an AI flow or architecture proposal as an open question of the project. */
  keepProposalQuestion: (projectId: string, proposalId: string, index: number) =>
    api<Question>('POST', `${base(projectId)}/questions/keep`, { proposalId, index }),
  answerQuestion: (projectId: string, id: string, answer: string) => api<Question>('POST', `${base(projectId)}/questions/${id}/answer`, { answer }),
  dropQuestion: (projectId: string, id: string) => api<Question>('POST', `${base(projectId)}/questions/${id}/drop`),
  deleteQuestion: (projectId: string, id: string) => api<void>('DELETE', `${base(projectId)}/questions/${id}`),

  /** `focus` holds the IDs of the parts the assistant looks at first. */
  critique: (projectId: string, branchId: string | null | undefined, question: string | null, focus: string[] = []) =>
    api<Critique | Unavailable>('POST', `${base(projectId)}/review/critiques`, { question, focus, branchId: branchId ?? null }),
  keepCritiqueQuestion: (projectId: string, critiqueId: string, index: number) =>
    api<Question>('POST', `${base(projectId)}/review/critiques/${critiqueId}/questions/${index}/keep`),
  keepCritiqueDecision: (projectId: string, critiqueId: string, index: number) =>
    api<Decision>('POST', `${base(projectId)}/review/critiques/${critiqueId}/proposed-decisions/${index}/keep`),
}

export const RESULT_LABEL: Record<RuleResult, string> = {
  verified: 'Verified',
  concern: 'Concern',
  unknown: 'Unknown',
  'not-applicable': 'Not applicable',
}

export const RESULT_COLOR: Record<RuleResult, string> = {
  verified: 'var(--trace-request)',
  concern: 'var(--trace-failure)',
  unknown: 'var(--trace-retry)',
  'not-applicable': 'var(--muted-foreground)',
}

export const SEVERITY_LABEL: Record<Severity, string> = { blocking: 'Blocking', warning: 'Warning', note: 'Note' }
export const CATEGORY_LABEL: Record<Category, string> = {
  resilience: 'Resilience', contracts: 'Contracts', truth: 'Truth', overengineering: 'Overengineering',
}

const SEVERITIES: Severity[] = ['blocking', 'warning', 'note']

export interface FindingFilter {
  results: RuleResult[]
  severities: Severity[]
  categories: Category[]
}

/** What the list shows first: the results that ask for attention. Verified and not-applicable results are one click away. */
export const DEFAULT_FILTER: FindingFilter = {
  results: ['concern', 'unknown'],
  severities: ['blocking', 'warning', 'note'],
  categories: ['resilience', 'contracts', 'truth', 'overengineering'],
}

export function filterFindings(findings: FindingView[], filter: FindingFilter): FindingView[] {
  return findings.filter(
    ({ finding }) => filter.results.includes(finding.result) && filter.severities.includes(finding.severity) && filter.categories.includes(finding.category),
  )
}

export interface FindingGroup {
  severity: Severity
  rules: { ruleId: string; findings: FindingView[] }[]
}

/** By severity (blocking first), then by rule ID. A severity with no finding has no group. */
export function groupFindings(findings: FindingView[]): FindingGroup[] {
  return SEVERITIES.map((severity) => {
    const rules = new Map<string, FindingView[]>()
    for (const view of findings.filter((f) => f.finding.severity === severity)) {
      rules.set(view.finding.ruleId, [...(rules.get(view.finding.ruleId) ?? []), view])
    }
    return { severity, rules: [...rules.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([ruleId, list]) => ({ ruleId, findings: list })) }
  }).filter((group) => group.rules.length > 0)
}

/** What a person did about a finding, in a few words. Null for an open finding. */
export function describeDisposition(view: FindingView): string | null {
  if (view.status === 'open') return null
  const what = view.status === 'accepted-by-decision' ? `Settled by ${view.decision ?? 'a decision'}` : "Won't fix"
  return view.needsReReview ? `${what} · needs re-review` : what
}

/**
 * The parts of an answer of the assistant, as the panel shows them. A risk is in `risks`
 * only when the server found a finding of a rule behind it. A claim with nothing behind it
 * arrives as a question with the source `unsupported-risk`: it goes to `suspicions` and is
 * never shown as a risk.
 */
export function critiqueSections(critique: Critique) {
  const indexed = critique.questions.map((question, index) => ({ question, index }))
  return {
    risks: critique.risks,
    questions: indexed.filter((q) => q.question.source !== 'unsupported-risk'),
    suspicions: indexed.filter((q) => q.question.source === 'unsupported-risk'),
    proposedDecisions: critique.proposedDecisions,
    leftOut: critique.leftOut,
  }
}

export function emptyDecision(supersedes: string | null = null): DecisionInput {
  return { title: '', context: '', decision: '', alternatives: [], consequences: null, affectedIds: [], settles: [], supersedes }
}
