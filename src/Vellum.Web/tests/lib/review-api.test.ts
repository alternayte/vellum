import { describe, it, expect } from 'vitest'
import {
  DEFAULT_FILTER, RESULT_LABEL, critiqueSections, describeDisposition, filterFindings, groupFindings,
  type Critique, type FindingView, type RuleResult, type Severity,
} from '../../src/lib/review-api'

function view(ruleId: string, severity: Severity, result: RuleResult, extra: Partial<FindingView> = {}): FindingView {
  return {
    finding: {
      ruleId, ruleVersion: 1, category: 'resilience', severity, result,
      subject: { kind: 'element', id: `${ruleId}-${result}`, name: 'Orders' }, cites: [],
      reason: '', wouldChange: '', question: null, fingerprint: 'f', fired: result === 'concern' || result === 'unknown',
    },
    status: 'open', decisionId: null, decision: null, reason: null, needsReReview: false, settled: false, blocks: false,
    ...extra,
  }
}

describe('findings list', () => {
  const findings = [
    view('REL-004', 'warning', 'concern'),
    view('REL-001', 'warning', 'verified'),
    view('OVR-003', 'note', 'concern'),
    view('TRU-001', 'blocking', 'concern'),
    view('CON-001', 'blocking', 'unknown'),
    view('REL-001', 'warning', 'unknown'),
    view('REL-003', 'warning', 'not-applicable'),
  ]

  it('hides verified and not-applicable results until they are asked for', () => {
    const shown = filterFindings(findings, DEFAULT_FILTER)
    expect(shown.map((f) => f.finding.result).sort()).toEqual(['concern', 'concern', 'concern', 'unknown', 'unknown'])
    expect(filterFindings(findings, { ...DEFAULT_FILTER, results: ['verified', 'not-applicable'] })).toHaveLength(2)
  })

  it('groups by severity with blocking first, then by rule ID', () => {
    const groups = groupFindings(filterFindings(findings, DEFAULT_FILTER))
    expect(groups.map((g) => g.severity)).toEqual(['blocking', 'warning', 'note'])
    expect(groups[0].rules.map((r) => r.ruleId)).toEqual(['CON-001', 'TRU-001'])
    expect(groups[1].rules.map((r) => r.ruleId)).toEqual(['REL-001', 'REL-004'])
  })

  it('names the four results and no other', () => {
    expect(RESULT_LABEL).toEqual({ verified: 'Verified', concern: 'Concern', unknown: 'Unknown', 'not-applicable': 'Not applicable' })
  })

  it('says when an answer needs a new review', () => {
    expect(describeDisposition(view('REL-001', 'warning', 'concern'))).toBeNull()
    expect(describeDisposition(view('REL-001', 'warning', 'concern', { status: 'accepted-by-decision', decision: 'ADR-0002', needsReReview: true })))
      .toBe('Settled by ADR-0002 · needs re-review')
    expect(describeDisposition(view('REL-001', 'warning', 'concern', { status: 'wont-fix' }))).toBe("Won't fix")
  })
})

describe('critique sections', () => {
  it('never lists a claim without a finding as a risk', () => {
    const critique = {
      risks: [{ about: [], claim: 'A failed delivery is lost.', ruleId: 'REL-001', subjectId: 's' }],
      questions: [
        { about: [], text: 'Who reads the dead letters?', why: null, source: 'assistant', note: null, keptAs: null },
        { about: [], text: 'The assistant suspects: the API has no authentication.', why: null, source: 'unsupported-risk', note: 'The model does not show it.', keptAs: null },
      ],
      proposedDecisions: [], leftOut: [],
    } as unknown as Critique

    const sections = critiqueSections(critique)
    expect(sections.risks.map((r) => r.claim)).toEqual(['A failed delivery is lost.'])
    expect(sections.suspicions.map((s) => s.index)).toEqual([1])
    expect(sections.questions.map((q) => q.index)).toEqual([0])
  })
})
