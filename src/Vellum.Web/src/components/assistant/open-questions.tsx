import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { reviewApi } from '@/lib/review-api'

interface Props {
  projectId: string
  proposalId: string
  questions: string[]
}

/**
 * The questions a proposal leaves open. "Keep" makes one an open question of the project,
 * where it stays after the proposal is closed. The server reads the text from the stored
 * proposal.
 */
export function OpenQuestions({ projectId, proposalId, questions }: Props) {
  const queryClient = useQueryClient()
  const [kept, setKept] = useState<Set<number>>(new Set())
  const keep = useMutation({
    mutationFn: (index: number) => reviewApi.keepProposalQuestion(projectId, proposalId, index),
    onSuccess: (_, index) => {
      setKept((before) => new Set(before).add(index))
      return queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'questions'] })
    },
    onError: shownInline,
  })

  return (
    <>
      <ul className="mt-1 space-y-1 text-xs text-foreground">
        {questions.map((question, index) => (
          <li key={index} className="flex items-start gap-2">
            <span className="min-w-0 flex-1 leading-relaxed">{question}</span>
            {kept.has(index) ? (
              <span className="shrink-0 text-[11px] text-muted-foreground">Kept</span>
            ) : (
              <Button variant="secondary" size="sm" className="h-6 shrink-0 px-2 text-xs" disabled={keep.isPending} onClick={() => keep.mutate(index)} title="Keep as an open question of the project">
                Keep
              </Button>
            )}
          </li>
        ))}
      </ul>
      {keep.isError && <p role="alert" className="mt-1 text-xs text-destructive">{keep.error.message}</p>}
    </>
  )
}
