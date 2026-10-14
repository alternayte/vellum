import { describe, expect, it } from 'vitest'
import { mergeProposalPages, type ProposalSummary } from './design-api'

const item = (id: string, createdAt: string): ProposalSummary => ({
  id, status: 'accepted', name: id, request: '', origin: 'ai', createdBy: 'u', createdAt,
  decidedBy: 'u', decidedAt: createdAt, items: null, acceptedItems: null, rejectedItems: null,
})

describe('mergeProposalPages', () => {
  it('hides what is older than the last loaded item of a kind that has more pages', () => {
    // The next page of flows may hold a flow made on day 3; the addition of day 2 must wait for it.
    const merged = mergeProposalPages({
      flow: { items: [item('f5', '2026-01-05'), item('f4', '2026-01-04')], cursor: 'next' },
      architecture: { items: [item('a6', '2026-01-06'), item('a2', '2026-01-02')], cursor: null },
    })
    expect(merged.items.map((i) => i.id)).toEqual(['a6', 'f5', 'f4'])
    expect(merged.items.map((i) => i.kind)).toEqual(['architecture', 'flow', 'flow'])
  })

  it('shows everything, newest first, when no kind has more', () => {
    const merged = mergeProposalPages({
      flow: { items: [item('f4', '2026-01-04')], cursor: null },
      architecture: { items: [item('a6', '2026-01-06'), item('a2', '2026-01-02')], cursor: null },
    })
    expect(merged.items.map((i) => i.id)).toEqual(['a6', 'f4', 'a2'])
  })
})
