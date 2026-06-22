import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useRelationships } from '../../src/hooks/use-relationships'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdRelationships: vi.fn(),
  postApiProjectsByProjectIdRelationships: vi.fn(),
  patchApiProjectsByProjectIdRelationshipsByRelationshipId: vi.fn(),
  deleteApiProjectsByProjectIdRelationshipsByRelationshipId: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import { getApiProjectsByProjectIdRelationships } from '../../src/api/generated'

const PROJECT_ID = 'proj-1'

const MOCK_RELATIONSHIPS = [
  { id: 'r1', fromId: '1', toId: '2', label: 'Places orders', technology: 'HTTPS' },
]

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
}

beforeEach(() => {
  vi.clearAllMocks()
})

describe('useRelationships', () => {
  it('fetches and returns relationships', async () => {
    vi.mocked(getApiProjectsByProjectIdRelationships).mockResolvedValue({
      data: { items: MOCK_RELATIONSHIPS, cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useRelationships(PROJECT_ID), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.data).toEqual(MOCK_RELATIONSHIPS)
    expect(getApiProjectsByProjectIdRelationships).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      query: { limit: 200 },
    })
  })
})
