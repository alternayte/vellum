import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  deleteApiProjectsByProjectIdScenariosByScenarioId,
  getApiProjectsByProjectIdScenarios,
  getApiProjectsByProjectIdScenariosByScenarioId,
  postApiProjectsByProjectIdScenarios,
  postApiProjectsByProjectIdScenariosByScenarioIdRun,
  putApiProjectsByProjectIdScenariosByScenarioId,
} from '@/api/generated'
import '@/api/client'
import type { TraceRun } from '@/lib/trace'
import { apiErrorMessage } from './use-contracts'

export interface ScenarioSummary {
  id: string
  name: string
}

/** The stored scenario document. Steps, faults and assertions are the engine's format. */
export interface ScenarioDocument {
  id: string
  name: string
  steps: unknown[]
  faults: unknown[]
  assertions: unknown[]
}

function unwrap<T>(result: { data?: unknown; error?: unknown }, fallback: string): T {
  if (result.error !== undefined) throw new Error(apiErrorMessage(result.error, fallback))
  return result.data as T
}

export function useScenarios(projectId: string) {
  return useQuery({
    queryKey: ['projects', projectId, 'scenarios'],
    queryFn: async () =>
      unwrap<{ items: ScenarioSummary[] }>(
        await getApiProjectsByProjectIdScenarios({ path: { projectId } }),
        'Scenarios did not load.',
      ).items,
    staleTime: 30_000,
  })
}

export async function fetchScenario(projectId: string, scenarioId: string): Promise<ScenarioDocument> {
  return unwrap<ScenarioDocument>(
    await getApiProjectsByProjectIdScenariosByScenarioId({ path: { projectId, scenarioId } }),
    'The scenario did not load.',
  )
}

export function useSaveScenario(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ scenario, isNew }: { scenario: ScenarioDocument; isNew: boolean }) => {
      // The document is authored as text, so its shape is checked by the server, not here.
      const body = scenario as never
      const result = isNew
        ? await postApiProjectsByProjectIdScenarios({ path: { projectId }, body })
        : await putApiProjectsByProjectIdScenariosByScenarioId({ path: { projectId, scenarioId: scenario.id }, body })
      return unwrap<ScenarioDocument>(result, 'The scenario was not saved.')
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'scenarios'] }),
  })
}

export function useDeleteScenario(projectId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (scenarioId: string) =>
      unwrap<void>(
        await deleteApiProjectsByProjectIdScenariosByScenarioId({ path: { projectId, scenarioId } }),
        'The scenario was not deleted.',
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'scenarios'] }),
  })
}

export function useRunScenario(projectId: string, branchId?: string | null) {
  return useMutation({
    mutationFn: async (scenarioId: string) =>
      unwrap<TraceRun>(
        await postApiProjectsByProjectIdScenariosByScenarioIdRun({
          path: { projectId, scenarioId },
          query: { branchId: branchId ?? undefined },
        }),
        'The scenario did not run.',
      ),
  })
}
