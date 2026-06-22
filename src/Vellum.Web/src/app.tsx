import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider, createRouter } from '@tanstack/react-router'
import { Toaster } from 'sonner'
import { toast } from 'sonner'
import { routeTree } from './routeTree.gen'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: 3,
    },
    mutations: {
      onError: (error: Error) => {
        const message = error.message ?? 'Something went wrong'
        if (message.includes('409') || message.includes('conflict')) {
          toast.error('This changed in another session. Reload to see the latest.', {
            action: { label: 'Reload', onClick: () => window.location.reload() },
          })
        } else if (message.includes('403')) {
          toast.error("You don't have permission for this action")
        } else if (message.includes('404')) {
          toast.error('Element not found — it may have been deleted')
        } else {
          toast.error(message)
        }
      },
    },
  },
})

const router = createRouter({ routeTree })

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
      <Toaster position="bottom-right" theme="dark" />
    </QueryClientProvider>
  )
}
