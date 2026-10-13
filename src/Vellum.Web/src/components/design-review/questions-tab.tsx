import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { reviewApi, type Question } from '@/lib/review-api'
import { areaClass } from './styles'

function QuestionRow({ projectId, question, onChanged }: { projectId: string; question: Question; onChanged: () => void }) {
  const [answering, setAnswering] = useState(false)
  const [answer, setAnswer] = useState('')
  const act = useMutation({
    mutationFn: (action: 'answer' | 'drop' | 'delete'): Promise<unknown> =>
      action === 'answer' ? reviewApi.answerQuestion(projectId, question.id, answer.trim())
        : action === 'drop' ? reviewApi.dropQuestion(projectId, question.id)
        : reviewApi.deleteQuestion(projectId, question.id),
    onSuccess: () => { setAnswering(false); onChanged() },
    onError: shownInline,
  })

  return (
    <li className="rounded-md border border-border px-2.5 py-2">
      <p className="text-xs leading-relaxed text-foreground">{question.text}</p>
      <p className="mt-1 flex flex-wrap items-center gap-x-2 text-[11px] text-muted-foreground">
        <span className="rounded-full border border-border px-1.5">{question.status}</span>
        <span>{question.origin}</span>
      </p>
      {question.answer && <p className="mt-1 text-xs leading-relaxed text-foreground"><span className="text-muted-foreground">Answer:</span> {question.answer}</p>}
      <div className="mt-1.5 flex flex-wrap gap-1.5">
        {question.status === 'open' && (
          <>
            <Button variant="secondary" size="sm" className="h-6 px-2 text-xs" onClick={() => setAnswering(!answering)}>Answer</Button>
            <Button variant="ghost" size="sm" className="h-6 px-2 text-xs" disabled={act.isPending} onClick={() => act.mutate('drop')}>Drop</Button>
          </>
        )}
        <Button variant="ghost" size="sm" className="h-6 px-2 text-xs" disabled={act.isPending} onClick={() => act.mutate('delete')}>Delete</Button>
      </div>
      {answering && (
        <form className="mt-1.5 space-y-1.5" onSubmit={(e) => { e.preventDefault(); act.mutate('answer') }}>
          <textarea className={areaClass} value={answer} onChange={(e) => setAnswer(e.target.value)} aria-label="Answer" required />
          <Button type="submit" size="sm" className="h-6 px-2 text-xs" disabled={act.isPending || answer.trim() === ''}>Save the answer</Button>
        </form>
      )}
      {act.isError && <p role="alert" className="mt-1 text-xs text-destructive">{act.error.message}</p>}
    </li>
  )
}

/** What the design leaves undecided. A person answers or drops a question. */
export function QuestionsTab({ projectId }: { projectId: string }) {
  const queryClient = useQueryClient()
  const questions = useQuery({ queryKey: ['projects', projectId, 'questions'], queryFn: () => reviewApi.questions(projectId) })
  const [text, setText] = useState('')
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'questions'] })
  const add = useMutation({
    mutationFn: () => reviewApi.addQuestion(projectId, text.trim()),
    onSuccess: () => { setText(''); return refresh() },
    onError: shownInline,
  })
  const list = questions.data ?? []
  const open = list.filter((q) => q.status === 'open')
  const closed = list.filter((q) => q.status !== 'open')

  return (
    <div className="space-y-3 p-3">
      <form className="space-y-1.5" onSubmit={(e) => { e.preventDefault(); add.mutate() }}>
        <label className="block text-[11px] font-medium text-muted-foreground">
          New question
          <textarea className={areaClass} value={text} onChange={(e) => setText(e.target.value)} placeholder="What does the design leave undecided?" required />
        </label>
        <Button type="submit" size="sm" className="h-6 px-2 text-xs" disabled={add.isPending || text.trim() === ''}>Add</Button>
        {add.isError && <p role="alert" className="text-xs text-destructive">{add.error.message}</p>}
      </form>
      {questions.isError && <p role="alert" className="text-xs text-destructive">{questions.error.message}</p>}
      {questions.isSuccess && list.length === 0 && <p className="text-xs text-muted-foreground">The project has no open question.</p>}

      {open.length > 0 && (
        <section aria-label="Open questions">
          <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Open <span className="font-mono">{open.length}</span></h3>
          <ul className="mt-1.5 space-y-1.5">{open.map((q) => <QuestionRow key={q.id} projectId={projectId} question={q} onChanged={refresh} />)}</ul>
        </section>
      )}
      {closed.length > 0 && (
        <section aria-label="Closed questions">
          <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Answered or dropped <span className="font-mono">{closed.length}</span></h3>
          <ul className="mt-1.5 space-y-1.5">{closed.map((q) => <QuestionRow key={q.id} projectId={projectId} question={q} onChanged={refresh} />)}</ul>
        </section>
      )}
    </div>
  )
}
