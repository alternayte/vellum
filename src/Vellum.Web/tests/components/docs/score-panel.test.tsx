import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ScorePanel } from '../../../src/components/docs/score-panel'
import type { Score } from '../../../src/hooks/use-scores'

const mockScore: Score = {
  id: '1',
  docId: 'doc-1',
  docType: 'adr',
  overallScore: 3.8,
  criteriaResults: [
    { key: 'context', name: 'Context', score: 4, maxScore: 5, explanation: 'Good', suggestedEdit: null },
    { key: 'decision', name: 'Decision', score: 3, maxScore: 5, explanation: 'Needs work', suggestedEdit: 'Add rationale' },
  ],
  suggestedContent: '# Revised ADR',
  scoredBy: 'user-1',
  createdAt: '2026-06-24T00:00:00Z',
}

describe('ScorePanel', () => {
  it('renders overall score and criteria when open with score', () => {
    render(
      <ScorePanel
        open={true}
        onOpenChange={() => {}}
        score={mockScore}
        scoreHistory={[]}
        onSelectScore={() => {}}
        onReviewSuggestions={() => {}}
        isLoading={false}
      />
    )
    expect(screen.getByText('3.8')).toBeDefined()
    expect(screen.getByText('Context')).toBeDefined()
    expect(screen.getByText('Decision')).toBeDefined()
    expect(screen.getByText('Review Suggestions')).toBeDefined()
  })

  it('renders loading state when scoring', () => {
    render(
      <ScorePanel
        open={true}
        onOpenChange={() => {}}
        score={null}
        scoreHistory={[]}
        onSelectScore={() => {}}
        onReviewSuggestions={() => {}}
        isLoading={true}
      />
    )
    expect(screen.getByText('Scoring document...')).toBeDefined()
  })
})
