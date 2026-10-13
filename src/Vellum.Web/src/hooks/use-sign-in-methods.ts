import { useQuery } from '@tanstack/react-query'
import { api } from '@/lib/settings-api'

export interface SignInMethods {
  password: boolean
  /** The external providers this instance has configured, as the sign-in path takes them. */
  providers: string[]
  registrationOpen: boolean
}

/** What this instance offers on its sign-in screen. The screens show nothing else. */
export function useSignInMethods() {
  return useQuery({ queryKey: ['auth', 'methods'], queryFn: () => api<SignInMethods>('GET', '/api/auth/methods'), staleTime: 60_000 })
}
