import { useState } from 'react'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useThreadActions, useThreads, type Thread, type ThreadStatus } from '@/hooks/use-threads'
import { useShellStore } from '@/stores/shell-store'
import { cn } from '@/lib/utils'
import { MentionInput } from './mention-input'
import { ThreadCard } from './thread-card'
import { useCommentAccess } from './use-comment-access'

type Filter = ThreadStatus | 'all'

const FILTERS: { id: Filter; label: string }[] = [
  { id: 'open', label: 'Open' },
  { id: 'resolved', label: 'Resolved' },
  { id: 'detached', label: 'Detached' },
  { id: 'all', label: 'All' },
]

/**
 * Every comment thread of the selected branch: on parts of the model, on the project, and
 * detached threads whose target was deleted. A thread on a part of the model selects it.
 */
export function CommentsFlyout({ projectId, projectName }: { projectId: string; projectName: string }) {
  const access = useCommentAccess(projectId)
  const { data: threads, isLoading, error } = useThreads(projectId, access.draftId)
  const actions = useThreadActions(projectId, access.draftId)
  const focusId = useShellStore((s) => s.focusThreadId)
  const [chosen, setChosen] = useState<Filter>('open')
  const [writing, setWriting] = useState(false)

  // A thread brought into view from the inbox shows whatever its status is.
  const focusedStatus = threads?.find((t) => t.id === focusId)?.status
  const filter: Filter = focusedStatus && chosen !== 'all' && chosen !== focusedStatus ? 'all' : chosen
  const shown = (threads ?? []).filter((t) => filter === 'all' || t.status === filter)
  const count = (f: Filter) => (threads ?? []).filter((t) => f === 'all' || t.status === f).length

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Comments</span>
        {access.canWrite && (
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setWriting(true)}>
            <Plus className="h-3 w-3" aria-hidden="true" />
            On the project
          </Button>
        )}
      </div>
      <div role="tablist" aria-label="Thread status" className="flex gap-1 border-b border-border px-2 py-1.5">
        {FILTERS.map((f) => (
          <button
            key={f.id}
            type="button"
            role="tab"
            aria-selected={filter === f.id}
            onClick={() => setChosen(f.id)}
            className={cn(
              'rounded px-1.5 py-0.5 text-[11px] focus-visible:outline-2 focus-visible:outline-primary',
              filter === f.id ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            {f.label} <span className="tabular-nums opacity-70">{count(f.id)}</span>
          </button>
        ))}
      </div>
      <div className="flex-1 space-y-2 overflow-y-auto p-2">
        {writing && (
          <MentionInput
            members={access.members}
            label={`New comment on ${projectName}`}
            placeholder={`Comment on the project ${projectName}…`}
            submitLabel="Comment"
            autoFocus
            pending={actions.create.isPending}
            onSubmit={(body, mentions) => actions.create.mutate(
              { target: { kind: 'project', id: projectId }, body, mentions },
              { onSuccess: () => { setWriting(false); setChosen('open') } },
            )}
            onCancel={() => setWriting(false)}
          />
        )}
        {isLoading ? (
          <p className="px-1 text-xs text-muted-foreground">Loading comments…</p>
        ) : error ? (
          <p role="alert" className="px-1 text-xs text-destructive">{error.message}</p>
        ) : shown.length === 0 ? (
          <p className="px-1 text-xs leading-relaxed text-muted-foreground">
            {(threads ?? []).length === 0
              ? 'No comments yet. Select an element or a relationship and comment in its panel, or comment on the project here.'
              : 'No thread has this status.'}
          </p>
        ) : (
          shown.map((thread) => (
            <div key={thread.id} className="space-y-1">
              <TargetLink thread={thread} />
              <ThreadCard projectId={projectId} thread={thread} access={access} showTarget />
            </div>
          ))
        )}
      </div>
    </div>
  )
}

/** Selects the target of a thread on the canvas, or opens its document page. */
function TargetLink({ thread }: { thread: Thread }) {
  const { selectElement, selectRelationship, selectMessage, openDoc, focusThread } = useShellStore()
  const select = thread.status === 'detached' ? null
    : thread.target.kind === 'element' ? selectElement
    : thread.target.kind === 'relationship' ? selectRelationship
    : thread.target.kind === 'message' ? selectMessage
    : thread.target.kind === 'document' ? openDoc
    : null
  if (!select) return null
  return (
    <button
      type="button"
      className="px-1 text-[11px] text-primary hover:underline"
      onClick={() => {
        select(thread.target.id)
        focusThread(thread.id)
      }}
    >
      Show {thread.target.name}
    </button>
  )
}
