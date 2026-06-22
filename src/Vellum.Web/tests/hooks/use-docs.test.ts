// src/Vellum.Web/tests/hooks/use-docs.test.ts
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useDocs, useDoc, useCreateDoc, useAutoSaveDoc } from '../../src/hooks/use-docs'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdDocs: vi.fn(),
  getApiProjectsByProjectIdDocsByDocId: vi.fn(),
  postApiProjectsByProjectIdDocs: vi.fn(),
  patchApiProjectsByProjectIdDocsByDocId: vi.fn(),
  deleteApiProjectsByProjectIdDocsByDocId: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import {
  getApiProjectsByProjectIdDocs,
  getApiProjectsByProjectIdDocsByDocId,
  postApiProjectsByProjectIdDocs,
  patchApiProjectsByProjectIdDocsByDocId,
} from '../../src/api/generated'

const PROJECT_ID = 'proj-1'
const DOC_ID = 'doc-1'

const MOCK_DOC = {
  id: DOC_ID, projectId: PROJECT_ID, spaceId: null, elementId: null,
  title: 'Test Doc', content: '# Hello', createdBy: 'user-1', createdAt: '', updatedAt: '',
}

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
}

beforeEach(() => { vi.clearAllMocks() })

describe('useDocs', () => {
  it('fetches and returns docs list', async () => {
    vi.mocked(getApiProjectsByProjectIdDocs).mockResolvedValue({
      data: { items: [MOCK_DOC], cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useDocs(PROJECT_ID), { wrapper: makeWrapper() })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.data).toEqual([MOCK_DOC])
  })
})

describe('useDoc', () => {
  it('fetches single doc when docId is provided', async () => {
    vi.mocked(getApiProjectsByProjectIdDocsByDocId).mockResolvedValue({
      data: MOCK_DOC,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useDoc(PROJECT_ID, DOC_ID), { wrapper: makeWrapper() })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.data).toEqual(MOCK_DOC)
  })

  it('does not fetch when docId is null', async () => {
    const { result } = renderHook(() => useDoc(PROJECT_ID, null), { wrapper: makeWrapper() })
    expect(result.current.isLoading).toBe(false)
    expect(getApiProjectsByProjectIdDocsByDocId).not.toHaveBeenCalled()
  })
})

describe('useCreateDoc', () => {
  it('calls post endpoint to create a doc', async () => {
    vi.mocked(postApiProjectsByProjectIdDocs).mockResolvedValue({
      data: MOCK_DOC,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useCreateDoc(PROJECT_ID), { wrapper: makeWrapper() })

    await act(async () => {
      await result.current.mutateAsync({ id: DOC_ID, title: 'Test Doc' })
    })

    expect(postApiProjectsByProjectIdDocs).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      body: { id: DOC_ID, title: 'Test Doc', spaceId: null, elementId: null, draftId: null, adrStatus: null },
    })
  })
})

describe('useAutoSaveDoc', () => {
  it('debounces save calls', async () => {
    vi.useFakeTimers()
    vi.mocked(patchApiProjectsByProjectIdDocsByDocId).mockResolvedValue({
      data: MOCK_DOC,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAutoSaveDoc(PROJECT_ID, DOC_ID), { wrapper: makeWrapper() })

    result.current.autoSave('# Updated')
    expect(patchApiProjectsByProjectIdDocsByDocId).not.toHaveBeenCalled()

    await act(async () => {
      vi.advanceTimersByTime(300)
      await vi.runAllTimersAsync()
    })

    expect(patchApiProjectsByProjectIdDocsByDocId).toHaveBeenCalled()
    vi.useRealTimers()
  })
})
