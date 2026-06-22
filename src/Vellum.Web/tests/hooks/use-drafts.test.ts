// src/Vellum.Web/tests/hooks/use-drafts.test.ts
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useDrafts, useDraft, useCreateDraft, useAbandonDraft } from '../../src/hooks/use-drafts'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdDrafts: vi.fn(),
  getApiProjectsByProjectIdDraftsByDraftId: vi.fn(),
  postApiProjectsByProjectIdDrafts: vi.fn(),
  postApiProjectsByProjectIdDraftsByDraftIdAbandon: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import {
  getApiProjectsByProjectIdDrafts,
  getApiProjectsByProjectIdDraftsByDraftId,
  postApiProjectsByProjectIdDrafts,
  postApiProjectsByProjectIdDraftsByDraftIdAbandon,
} from '../../src/api/generated'

const PROJECT_ID = 'proj-1'
const DRAFT_ID = 'draft-1'

const MOCK_DRAFT = {
  id: DRAFT_ID,
  projectId: PROJECT_ID,
  name: 'My Draft',
  streamId: 'stream-2',
  baseStreamId: 'stream-1',
  forkVersion: 3,
  status: 'active',
  createdBy: 'user-1',
  createdAt: '2024-01-01T00:00:00Z',
  mergedAt: null,
  abandonedAt: null,
}

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
}

beforeEach(() => { vi.clearAllMocks() })

describe('useDrafts', () => {
  it('fetches and returns drafts list', async () => {
    vi.mocked(getApiProjectsByProjectIdDrafts).mockResolvedValue({
      data: { items: [MOCK_DRAFT], cursor: null },
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useDrafts(PROJECT_ID), { wrapper: makeWrapper() })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.data).toEqual({ items: [MOCK_DRAFT], cursor: null })
  })
})

describe('useDraft', () => {
  it('fetches single draft when draftId is provided', async () => {
    vi.mocked(getApiProjectsByProjectIdDraftsByDraftId).mockResolvedValue({
      data: MOCK_DRAFT,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useDraft(PROJECT_ID, DRAFT_ID), { wrapper: makeWrapper() })
    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.data).toEqual(MOCK_DRAFT)
  })

  it('does not fetch when draftId is null', async () => {
    const { result } = renderHook(() => useDraft(PROJECT_ID, null), { wrapper: makeWrapper() })
    expect(result.current.isLoading).toBe(false)
    expect(getApiProjectsByProjectIdDraftsByDraftId).not.toHaveBeenCalled()
  })
})

describe('useCreateDraft', () => {
  it('calls post endpoint to create a draft', async () => {
    vi.mocked(postApiProjectsByProjectIdDrafts).mockResolvedValue({
      data: MOCK_DRAFT,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useCreateDraft(PROJECT_ID), { wrapper: makeWrapper() })

    await act(async () => {
      await result.current.mutateAsync({ id: DRAFT_ID, name: 'My Draft' })
    })

    expect(postApiProjectsByProjectIdDrafts).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      body: { id: DRAFT_ID, name: 'My Draft' },
    })
  })
})

describe('useAbandonDraft', () => {
  it('calls abandon endpoint', async () => {
    vi.mocked(postApiProjectsByProjectIdDraftsByDraftIdAbandon).mockResolvedValue({
      data: { ...MOCK_DRAFT, status: 'abandoned', abandonedAt: '2024-01-02T00:00:00Z' },
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAbandonDraft(PROJECT_ID), { wrapper: makeWrapper() })

    await act(async () => {
      await result.current.mutateAsync(DRAFT_ID)
    })

    expect(postApiProjectsByProjectIdDraftsByDraftIdAbandon).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID, draftId: DRAFT_ID },
    })
  })
})
