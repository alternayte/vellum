// src/Vellum.Web/tests/hooks/use-spaces.test.ts
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useSpaces, useCreateSpace } from '../../src/hooks/use-spaces'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdSpaces: vi.fn(),
  postApiProjectsByProjectIdSpaces: vi.fn(),
  patchApiProjectsByProjectIdSpacesBySpaceId: vi.fn(),
  deleteApiProjectsByProjectIdSpacesBySpaceId: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import {
  getApiProjectsByProjectIdSpaces,
  postApiProjectsByProjectIdSpaces,
} from '../../src/api/generated'

const PROJECT_ID = 'proj-1'

const MOCK_SPACES = [
  { id: 'sp-1', projectId: PROJECT_ID, name: 'ADRs', createdAt: '', updatedAt: '' },
]

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
}

beforeEach(() => { vi.clearAllMocks() })

describe('useSpaces', () => {
  it('fetches and returns spaces list', async () => {
    vi.mocked(getApiProjectsByProjectIdSpaces).mockResolvedValue({
      data: MOCK_SPACES,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useSpaces(PROJECT_ID), { wrapper: makeWrapper() })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.data).toEqual(MOCK_SPACES)
  })
})

describe('useCreateSpace', () => {
  it('calls post endpoint to create a space', async () => {
    vi.mocked(postApiProjectsByProjectIdSpaces).mockResolvedValue({
      data: MOCK_SPACES[0],
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useCreateSpace(PROJECT_ID), { wrapper: makeWrapper() })

    await act(async () => {
      await result.current.mutateAsync({ id: 'sp-1', name: 'ADRs' })
    })

    expect(postApiProjectsByProjectIdSpaces).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      body: { id: 'sp-1', name: 'ADRs' },
    })
  })
})
