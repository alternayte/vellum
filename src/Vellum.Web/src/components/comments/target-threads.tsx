import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useShellStore } from '@/stores/shell-store'
import { useThreadActions, useThreads, type TargetKind } from '@/hooks/use-threads'
import { MentionInput } from './mention-input'
import { ThreadCard } from './thread-card'
import { useCommentAccess } from './use-comment-access'

const HEADING = 'mb-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground'

/**
 * The comment threads of one target, in the detail panel, and a new thread on it. The
 * threads are those of the selected branch: main, or the selected draft.
 */
export function TargetThreads({ projectId, kind, id, stepId, name }: {
  projectId: string
  kind: TargetKind
  id: string
  /** The step ID, for a flow step. `id` is then the scenario ID. */
  stepId?: string
  name: string
}) {
  const access = useCommentAccess(projectId)
  const { data: threads, isLoading, error } = useThreads(projectId, access.draftId)
  const actions = useThreadActions(projectId, access.draftId)
  const [writing, setWriting] = useState(false)
  const [showResolved, setShowResolved] = useState(false)
  const focusId = useShellStore((s) => s.focusThreadId)
  const headingId = `threads-${id}${stepId ? `-${stepId}` : ''}`

  const mine = (threads ?? []).filter((t) => t.target.kind === kind && t.target.id === id && (kind !== 'step' || t.target.stepId === stepId))
  const resolved = mine.filter((t) => t.status === 'resolved')
  const shown = showResolved || resolved.some((t) => t.id === focusId) ? mine : mine.filter((t) => t.status !== 'resolved')

  return (
    <section aria-labelledby={headingId} className="space-y-2">
      <div className="flex items-center gap-2">
        <h3 id={headingId} className={`${HEADING} mb-0`}>Comments</h3>
        {resolved.length > 0 && (
          <button type="button" className="ml-auto text-[11px] text-muted-foreground hover:text-foreground" onClick={() => setShowResolved((v) => !v)}>
            {showResolved ? 'Hide resolved' : `Show ${resolved.length} resolved`}
          </button>
        )}
      </div>
      {isLoading ? (
        <p className="text-xs text-muted-foreground">Loading comments…</p>
      ) : error ? (
        <p role="alert" className="text-xs text-destructive">{error.message}</p>
      ) : shown.length === 0 ? (
        <p className="text-xs text-muted-foreground">{mine.length === 0 ? 'No comments on this yet.' : 'Every thread here is resolved.'}</p>
      ) : (
        <div className="space-y-2">
          {shown.map((thread) => <ThreadCard key={thread.id} projectId={projectId} thread={thread} access={access} />)}
        </div>
      )}
      {access.canWrite && (
        writing ? (
          <MentionInput
            members={access.members}
            label={`New comment on ${name}`}
            placeholder={`Comment on ${name}…`}
            submitLabel="Comment"
            autoFocus
            pending={actions.create.isPending}
            onSubmit={(body, mentions) => actions.create.mutate({ target: { kind, id, stepId: kind === 'step' ? stepId : null }, body, mentions }, { onSuccess: () => setWriting(false) })}
            onCancel={() => setWriting(false)}
          />
        ) : (
          <Button size="sm" variant="outline" className="h-7 px-2 text-xs" onClick={() => setWriting(true)}>New comment</Button>
        )
      )}
    </section>
  )
}
