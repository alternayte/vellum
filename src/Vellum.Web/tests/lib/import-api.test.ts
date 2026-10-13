import { describe, it, expect } from 'vitest'
import { describeScan, withDependencies, type Proposal, type ScanSummary } from '../../src/lib/import-api'

const proposal = (id: string, dependsOn: string[] = [], status: Proposal['status'] = 'pending'): Proposal => ({
  id, action: 'add', entityKind: 'operation', entityId: id, factKey: id, name: id, proposed: null, current: null,
  changes: [], evidence: [], dependsOn, status, partial: false, canDecide: true, canDecideReason: null, impact: null, truncated: false,
})

const scan = (extra: Partial<ScanSummary>): ScanSummary => ({
  id: 's', repository: 'acme/orders', kind: 'tracking', trackedRef: 'main', canDecide: true, canDecideReason: null, coverage: null, relinked: 0, needsReview: 0, ref: null, commitSha: null, status: 'completed', isComplete: true,
  progress: { filesListed: 10, filesRead: 4, filesSkipped: 1, bytesRead: 0 },
  proposalCounts: { pending: 0, accepted: 0, rejected: 0 }, error: null, retryAt: null,
  requestedBy: 'u', createdAt: '', completedAt: null, ...extra,
})

describe('import selection', () => {
  it('adds every pending proposal a selected one depends on, through the chain', () => {
    const all = [proposal('service'), proposal('operation', ['service']), proposal('route-schema', ['operation']), proposal('other')]
    expect([...withDependencies(new Set(['route-schema']), all)].sort()).toEqual(['operation', 'route-schema', 'service'])
  })

  it('does not add a dependency that is accepted already', () => {
    const all = [proposal('service', [], 'accepted'), proposal('operation', ['service'])]
    expect([...withDependencies(new Set(['operation']), all)]).toEqual(['operation'])
  })
})

describe('scan wording', () => {
  it('never calls a partial scan complete', () => {
    expect(describeScan(scan({ isComplete: false }))).toMatch(/^Partial scan/)
    expect(describeScan(scan({}))).toBe('Scan complete')
  })

  it('reports progress while running', () => {
    expect(describeScan(scan({ status: 'running' }))).toBe('Reading files: 4 of 10')
  })
})
