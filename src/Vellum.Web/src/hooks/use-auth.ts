import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getApiAuthMe, postApiAuthLogin, postApiAuthRegister, postApiAuthLogout } from '@/api/generated'
import '@/api/client' // ensure client is configured

interface User {
  id: string
  email: string
  displayName: string | null
}

async function fetchMe(): Promise<User> {
  const result = await getApiAuthMe()
  if (!result.response?.ok) throw new Error('Not authenticated')
  return result.data as User
}

export function useAuth() {
  const queryClient = useQueryClient()

  const { data: user, isLoading, error } = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: fetchMe,
    retry: false,
    staleTime: 60_000,
  })

  const loginMutation = useMutation({
    mutationFn: async (credentials: { email: string; password: string }) => {
      const result = await postApiAuthLogin({ body: credentials })
      if (!result.response?.ok) throw new Error('Login failed')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['auth'] }),
  })

  const registerMutation = useMutation({
    mutationFn: async (data: { email: string; password: string; displayName?: string }) => {
      const result = await postApiAuthRegister({
        body: { email: data.email, password: data.password, displayName: data.displayName ?? null },
      })
      if (!result.response?.ok) throw new Error('Registration failed')
      return result.data as User
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['auth'] }),
  })

  const logoutMutation = useMutation({
    mutationFn: async () => {
      await postApiAuthLogout({})
    },
    onSuccess: () => queryClient.clear(),
  })

  return {
    user: user ?? null,
    isLoading,
    isAuthenticated: !!user && !error,
    login: loginMutation.mutateAsync,
    register: registerMutation.mutateAsync,
    logout: logoutMutation.mutateAsync,
    loginError: loginMutation.error,
    registerError: registerMutation.error,
  }
}
