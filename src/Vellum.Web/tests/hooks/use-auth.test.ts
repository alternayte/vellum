import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createElement } from 'react'
import { useAuth } from '../../src/hooks/use-auth'

// Mock the generated API functions
vi.mock('../../src/api/generated', () => ({
  getApiAuthMe: vi.fn(),
  postApiAuthLogin: vi.fn(),
  postApiAuthRegister: vi.fn(),
  postApiAuthLogout: vi.fn(),
}))

// Mock the client side-effect import
vi.mock('../../src/api/client', () => ({}))

import {
  getApiAuthMe,
  postApiAuthLogin,
  postApiAuthRegister,
} from '../../src/api/generated'

function makeWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
}

const MOCK_USER = { id: 'u1', email: 'a@b.com', displayName: 'Alice' }

beforeEach(() => {
  vi.clearAllMocks()
})

describe('useAuth', () => {
  it('returns user when /api/auth/me succeeds', async () => {
    vi.mocked(getApiAuthMe).mockResolvedValue({
      data: MOCK_USER,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAuth(), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.user).toEqual(MOCK_USER)
    expect(result.current.isAuthenticated).toBe(true)
  })

  it('returns null user when /api/auth/me returns non-ok', async () => {
    vi.mocked(getApiAuthMe).mockResolvedValue({
      data: null,
      response: { ok: false } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAuth(), { wrapper: makeWrapper() })

    await waitFor(() => expect(result.current.isLoading).toBe(false))

    expect(result.current.user).toBeNull()
    expect(result.current.isAuthenticated).toBe(false)
  })

  it('login calls postApiAuthLogin with credentials', async () => {
    vi.mocked(getApiAuthMe).mockResolvedValue({
      data: MOCK_USER,
      response: { ok: true } as Response,
      error: undefined,
    })
    vi.mocked(postApiAuthLogin).mockResolvedValue({
      data: undefined,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAuth(), { wrapper: makeWrapper() })

    await result.current.login({ email: 'a@b.com', password: 'secret' })

    expect(postApiAuthLogin).toHaveBeenCalledWith({
      body: { email: 'a@b.com', password: 'secret' },
    })
  })

  it('register calls postApiAuthRegister with registration data', async () => {
    vi.mocked(getApiAuthMe).mockResolvedValue({
      data: null,
      response: { ok: false } as Response,
      error: undefined,
    })
    vi.mocked(postApiAuthRegister).mockResolvedValue({
      data: MOCK_USER,
      response: { ok: true } as Response,
      error: undefined,
    })

    const { result } = renderHook(() => useAuth(), { wrapper: makeWrapper() })

    await result.current.register({ email: 'a@b.com', password: 'secret', displayName: 'Alice' })

    expect(postApiAuthRegister).toHaveBeenCalledWith({
      body: { email: 'a@b.com', password: 'secret', displayName: 'Alice' },
    })
  })
})
