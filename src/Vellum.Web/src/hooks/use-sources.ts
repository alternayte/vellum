import { useQuery } from '@tanstack/react-query'
import { isActive, sourcesApi } from '@/lib/import-api'

export const sourcesKey = (projectId: string) => ['projects', projectId, 'sources'] as const

/** The Sources of a project, polled fast while one of them scans and slowly otherwise. */
export function useSources(projectId: string) {
  return useQuery({
    queryKey: sourcesKey(projectId),
    queryFn: () => sourcesApi.list(projectId),
    refetchInterval: (query) => (query.state.data?.some((s) => s.latestScan && isActive(s.latestScan.status)) ? 2000 : 15000),
    refetchIntervalInBackground: true,
  })
}
