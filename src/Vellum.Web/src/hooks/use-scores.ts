import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'

export interface CriterionResult {
  key: string
  name: string
  score: number
  maxScore: number
  explanation: string
  suggestedEdit: string | null
}

export interface Score {
  id: string
  docId: string
  docType: string
  overallScore: number
  criteriaResults: CriterionResult[]
  suggestedContent: string | null
  scoredBy: string
  createdAt: string
}

export interface ScoreListItem {
  id: string
  docId: string
  docType: string
  overallScore: number
  scoredBy: string
  createdAt: string
}

export function useScoringStatus() {
  return useQuery({
    queryKey: ['scoring', 'status'],
    queryFn: async () => {
      const res = await fetch('/api/scoring/status')
      return (await res.json()) as { available: boolean }
    },
    staleTime: Infinity,
  })
}

export function useScores(projectId: string, docId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'docs', docId, 'scores'],
    queryFn: async () => {
      const res = await fetch(`/api/projects/${projectId}/docs/${docId}/scores`)
      if (!res.ok) throw new Error('Failed to load scores')
      return (await res.json()) as ScoreListItem[]
    },
    enabled: !!docId,
  })
}

export function useScore(projectId: string, docId: string, scoreId: string | null) {
  return useQuery({
    queryKey: ['projects', projectId, 'docs', docId, 'scores', scoreId],
    queryFn: async () => {
      const res = await fetch(`/api/projects/${projectId}/docs/${docId}/scores/${scoreId}`)
      if (!res.ok) throw new Error('Failed to load score')
      return (await res.json()) as Score
    },
    enabled: !!scoreId,
  })
}

export function useCreateScore(projectId: string, docId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async () => {
      const res = await fetch(`/api/projects/${projectId}/docs/${docId}/scores`, {
        method: 'POST',
      })
      if (!res.ok) {
        const error = await res.json().catch(() => ({ title: 'Scoring failed' }))
        throw new Error(error.title ?? error.detail ?? 'Scoring failed')
      }
      return (await res.json()) as Score
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'docs', docId, 'scores'] })
    },
  })
}
