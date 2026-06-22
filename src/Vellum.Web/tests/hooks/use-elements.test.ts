import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useElements, useAddElement, useRemoveElement } from '../../src/hooks/use-elements'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdElements: vi.fn(),
  postApiProjectsByProjectIdElements: vi.fn(),
  patchApiProjectsByProjectIdElementsByElementId: vi.fn(),
  deleteApiProjectsByProjectIdElementsByElementId: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import {
  getApiProjectsByProjectIdElements,
  postApiProjectsByProjectIdElements,
  deleteApiProjectsByProjectIdElementsByElementId,
} from '../../src/api/generated'

const PROJECT_ID = 'proj-1'

const MOCK_ELEMENTS = [
  { id: '1', kind: 'actor', name: 'Customer', description: null, technology: null, ownerId: null, status: 'current', parentId: null, tags: [] },
  { id: '2', kind: 'system', name: 'Orders', description: null, technology: 'dotnet', ownerId: null, status: 'current', parentId: null, tags: [] },
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

describe('useElements', () => {
  it('fetches and returns elements', async () => {
    vi.mocked(getApiProjectsByProjectIdElements).mockResolvedValue({
      data: { items: MOCK_ELEMENTS, cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useElements(PROJECT_ID), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.data).toEqual(MOCK_ELEMENTS)
    expect(getApiProjectsByProjectIdElements).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      query: { limit: 200 },
    })
  })
})

describe('useAddElement', () => {
  it('calls post endpoint and applies optimistic update', async () => {
    vi.mocked(getApiProjectsByProjectIdElements).mockResolvedValue({
      data: { items: MOCK_ELEMENTS, cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })
    vi.mocked(postApiProjectsByProjectIdElements).mockResolvedValue({
      data: { id: '3', kind: 'store', name: 'DB', description: null, technology: null, ownerId: null, status: 'current', parentId: '2', tags: [] },
      response: { ok: true } as Response,
      error: undefined,
    })

    const wrapper = makeWrapper()
    const { result: elementsResult } = renderHook(() => useElements(PROJECT_ID), { wrapper })
    const { result: addResult } = renderHook(() => useAddElement(PROJECT_ID), { wrapper })

    await waitFor(() => expect(elementsResult.current.isLoading).toBe(false))

    await act(async () => {
      await addResult.current.mutateAsync({ id: '3', kind: 'store', name: 'DB', parentId: '2' })
    })

    expect(postApiProjectsByProjectIdElements).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      body: expect.objectContaining({ id: '3', kind: 'store', name: 'DB', parentId: '2' }),
    })
  })
})

describe('useRemoveElement', () => {
  it('calls delete endpoint', async () => {
    vi.mocked(getApiProjectsByProjectIdElements).mockResolvedValue({
      data: { items: MOCK_ELEMENTS, cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })
    vi.mocked(deleteApiProjectsByProjectIdElementsByElementId).mockResolvedValue({
      data: undefined,
      response: { ok: true } as Response,
      error: undefined,
    })

    const wrapper = makeWrapper()
    const { result: elementsResult } = renderHook(() => useElements(PROJECT_ID), { wrapper })
    const { result: removeResult } = renderHook(() => useRemoveElement(PROJECT_ID), { wrapper })

    await waitFor(() => expect(elementsResult.current.isLoading).toBe(false))

    await act(async () => {
      await removeResult.current.mutateAsync('1')
    })

    expect(deleteApiProjectsByProjectIdElementsByElementId).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID, elementId: '1' },
    })
  })
})
