import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useViews, useView, useCreateView, useSaveLayout } from '../../src/hooks/use-views'

vi.mock('../../src/api/generated', () => ({
  getApiProjectsByProjectIdViews: vi.fn(),
  getApiProjectsByProjectIdViewsByViewId: vi.fn(),
  postApiProjectsByProjectIdViews: vi.fn(),
  putApiProjectsByProjectIdViewsByViewIdLayout: vi.fn(),
}))

vi.mock('../../src/api/client', () => ({}))

import {
  getApiProjectsByProjectIdViews,
  getApiProjectsByProjectIdViewsByViewId,
  postApiProjectsByProjectIdViews,
  putApiProjectsByProjectIdViewsByViewIdLayout,
} from '../../src/api/generated'

const PROJECT_ID = 'proj-1'
const VIEW_ID = 'view-1'

const MOCK_VIEWS = [
  { id: VIEW_ID, projectId: PROJECT_ID, name: 'Context View', rootElementId: null, visibleElementIds: [], activeLens: null, activeFlowId: null, createdAt: '', updatedAt: '' },
]

const MOCK_VIEW_DETAIL = {
  ...MOCK_VIEWS[0],
  positions: [{ elementId: '1', x: 100, y: 200 }],
  edges: [],
}

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

describe('useViews', () => {
  it('fetches and returns views list', async () => {
    vi.mocked(getApiProjectsByProjectIdViews).mockResolvedValue({
      data: MOCK_VIEWS,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useViews(PROJECT_ID), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.data).toEqual(MOCK_VIEWS)
  })
})

describe('useView', () => {
  it('fetches view detail when viewId is provided', async () => {
    vi.mocked(getApiProjectsByProjectIdViewsByViewId).mockResolvedValue({
      data: MOCK_VIEW_DETAIL,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useView(PROJECT_ID, VIEW_ID), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.data).toEqual(MOCK_VIEW_DETAIL)
    expect(getApiProjectsByProjectIdViewsByViewId).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID, viewId: VIEW_ID },
    })
  })

  it('does not fetch when viewId is null', async () => {
    const { result } = renderHook(() => useView(PROJECT_ID, null), { wrapper: makeWrapper() })

    // isLoading is false because enabled:false means it never starts
    expect(result.current.isLoading).toBe(false)
    expect(getApiProjectsByProjectIdViewsByViewId).not.toHaveBeenCalled()
  })
})

describe('useCreateView', () => {
  it('calls post endpoint to create a view', async () => {
    vi.mocked(postApiProjectsByProjectIdViews).mockResolvedValue({
      data: MOCK_VIEWS[0],
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useCreateView(PROJECT_ID), { wrapper: makeWrapper() })

    await act(async () => {
      await result.current.mutateAsync({ id: VIEW_ID, name: 'Context View' })
    })

    expect(postApiProjectsByProjectIdViews).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID },
      body: { id: VIEW_ID, name: 'Context View', rootElementId: null },
    })
  })
})

describe('useSaveLayout', () => {
  it('calls put layout endpoint after debounce', async () => {
    vi.useFakeTimers()
    vi.mocked(putApiProjectsByProjectIdViewsByViewIdLayout).mockResolvedValue({
      data: undefined,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useSaveLayout(PROJECT_ID, VIEW_ID), { wrapper: makeWrapper() })

    const positions = [{ elementId: '1', x: 100, y: 200 }]
    result.current.saveLayout(positions)

    // Should not have been called yet (debounced)
    expect(putApiProjectsByProjectIdViewsByViewIdLayout).not.toHaveBeenCalled()

    await act(async () => {
      vi.advanceTimersByTime(300)
      await vi.runAllTimersAsync()
    })

    expect(putApiProjectsByProjectIdViewsByViewIdLayout).toHaveBeenCalledWith({
      path: { projectId: PROJECT_ID, viewId: VIEW_ID },
      body: { positions, edges: null },
    })

    vi.useRealTimers()
  })

  it('does not call layout endpoint when viewId is null', async () => {
    vi.useFakeTimers()

    const { result } = renderHook(() => useSaveLayout(PROJECT_ID, null), { wrapper: makeWrapper() })

    result.current.saveLayout([{ elementId: '1', x: 0, y: 0 }])

    await act(async () => {
      vi.advanceTimersByTime(300)
      await vi.runAllTimersAsync()
    })

    expect(putApiProjectsByProjectIdViewsByViewIdLayout).not.toHaveBeenCalled()

    vi.useRealTimers()
  })
})
