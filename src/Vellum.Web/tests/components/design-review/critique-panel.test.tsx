import { describe, it, expect, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { CritiquePanel } from '../../../src/components/design-review/assistant-tab'
import type { Critique } from '../../../src/lib/review-api'

const critique: Critique = {
  id: 'c1', branchId: null, question: '',
  risks: [{ about: [{ kind: 'subscription', id: 's1', name: 'Billing on order-events' }], claim: 'A failed delivery to Billing is lost.', ruleId: 'REL-001', subjectId: 's1' }],
  questions: [
    { about: [], text: 'Who reads the dead-letter queue?', why: 'Nothing consumes it.', source: 'assistant', note: null, keptAs: null },
    {
      about: [], text: 'The assistant suspects: the Orders API has no authentication.', why: null, source: 'unsupported-risk',
      note: 'The model does not show it: the assistant gave no basis.', keptAs: null,
    },
  ],
  proposedDecisions: [],
  leftOut: [{ where: 'risk:2', reason: "It states that something is 'secure'. The assistant cannot establish that." }],
  modelVersion: 3, stale: false, findingsSent: 4, findingsLeftOut: 0, omittedPrivate: null,
  provider: { kind: 'openai', model: 'm', reportedModel: null }, createdAt: '2026-10-13T00:00:00Z',
}

describe('CritiquePanel', () => {
  it('labels the answer as suggestions and keeps an unsupported claim out of the risks', () => {
    render(<CritiquePanel critique={critique} keeping={false} onKeepQuestion={vi.fn()} onKeepDecision={vi.fn()} focusTarget={() => null} onFocus={vi.fn()} />)

    expect(screen.getByRole('region', { name: 'Suggestions of the assistant' })).toBeTruthy()
    expect(screen.getByText(/These are suggestions, not findings/)).toBeTruthy()

    const risks = within(screen.getByTestId('critique-risks'))
    expect(risks.getByText('A failed delivery to Billing is lost.')).toBeTruthy()
    expect(risks.queryByText(/no authentication/)).toBeNull()

    const suspicions = within(screen.getByTestId('critique-suspicions'))
    expect(suspicions.getByText(/no authentication/)).toBeTruthy()
    expect(suspicions.getByText('The model does not show it: the assistant gave no basis.')).toBeTruthy()
  })
})
