import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { useThreadActions, type Thread, type ThreadComment } from '@/hooks/use-threads'
import { useShellStore } from '@/stores/shell-store'
import { cn } from '@/lib/utils'
import { ConfirmDialog } from '@/components/shell/confirm-dialog'
import { MentionInput } from './mention-input'
import type { CommentAccess } from './use-comment-access'

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })

const STATUS: Record<Thread['status'], { label: string; className: string }> = {
  open: { label: 'Open', className: 'border-primary/40 text-primary' },
  resolved: { label: 'Resolved', className: 'border-border text-muted-foreground' },
  detached: { label: 'Detached', className: 'border-amber-500/50 text-amber-500' },
}

const TARGET_KIND_LABEL: Record<string, string> = {
  project: 'Project', element: 'Element', relationship: 'Relationship', message: 'Message', operation: 'Operation',
  channel: 'Channel', step: 'Flow step', promise: 'Promise', document: 'Document', decision: 'Decision',
}

interface ThreadCardProps {
  projectId: string
  thread: Thread
  access: CommentAccess
  /** Shows the target of the thread above it: for a list that holds threads of many targets. */
  showTarget?: boolean
}

/** One thread: its status, its comments with one level of replies, and what the person may do with it. */
export function ThreadCard({ projectId, thread, access, showTarget }: ThreadCardProps) {
  const actions = useThreadActions(projectId, thread.draftId)
  const [replying, setReplying] = useState(false)
  const [deciding, setDeciding] = useState(false)
  const focused = useShellStore((s) => s.focusThreadId === thread.id)
  const card = useRef<HTMLElement>(null)

  useEffect(() => {
    if (!focused) return
    card.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
    const done = window.setTimeout(() => useShellStore.getState().focusThread(null), 2500)
    return () => window.clearTimeout(done)
  }, [focused])

  const isAuthor = access.userId === thread.createdBy
  const canResolve = access.canWrite && (isAuthor || access.isEditor)
  const status = STATUS[thread.status]

  return (
    <article
      ref={card}
      aria-label={`Thread on ${thread.target.name}`}
      className={cn('rounded-md border border-border bg-muted/20 text-xs transition-shadow', focused && 'ring-2 ring-primary')}
    >
      <header className="flex items-center gap-2 border-b border-border px-2.5 py-1.5">
        <span className={cn('rounded-full border px-1.5 py-px text-[10px] font-medium', status.className)}>{status.label}</span>
        {(showTarget || thread.status === 'detached') && (
          <span className="min-w-0 truncate text-muted-foreground" title={thread.status === 'detached' ? 'The target was deleted. This is its last known name.' : undefined}>
            {TARGET_KIND_LABEL[thread.target.kind] ?? thread.target.kind} · <span className="text-foreground">{thread.target.name}</span>
          </span>
        )}
        {canResolve && thread.status !== 'detached' && (
          <button
            type="button"
            className="ml-auto shrink-0 rounded px-1.5 py-0.5 text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            disabled={actions.setStatus.isPending}
            onClick={() => actions.setStatus.mutate({ threadId: thread.id, resolved: thread.status === 'open' })}
          >
            {thread.status === 'open' ? 'Resolve' : 'Reopen'}
          </button>
        )}
      </header>

      <ol className="space-y-2.5 px-2.5 py-2">
        {thread.comments.map((comment, index) => (
          <CommentRow key={comment.id} projectId={projectId} thread={thread} comment={comment} access={access} reply={index > 0} />
        ))}
      </ol>

      {(thread.raisedQuestionId || thread.raisedDecisionId) && (
        <p className="border-t border-border px-2.5 py-1.5 text-[11px] text-muted-foreground">
          Raised as {[thread.raisedQuestionId && 'an open question', thread.raisedDecisionId && 'a proposed decision'].filter(Boolean).join(' and ')}. Review holds it.
        </p>
      )}

      {access.canWrite && (
        <footer className="space-y-2 border-t border-border px-2.5 py-2">
          {replying ? (
            <MentionInput
              members={access.members}
              label="Reply"
              placeholder="Reply…"
              submitLabel="Reply"
              autoFocus
              pending={actions.reply.isPending}
              onSubmit={(body, mentions) => actions.reply.mutate({ threadId: thread.id, body, mentions }, { onSuccess: () => setReplying(false) })}
              onCancel={() => setReplying(false)}
            />
          ) : (
            <div className="flex flex-wrap gap-1">
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setReplying(true)}>Reply</Button>
              {!thread.raisedQuestionId && (
                <Button
                  size="sm" variant="ghost" className="h-6 px-2 text-xs" disabled={actions.raiseQuestion.isPending}
                  onClick={() => actions.raiseQuestion.mutate(thread.id, { onSuccess: () => toast.success('The thread is an open question in Review.') })}
                >
                  Raise as open question
                </Button>
              )}
              {access.isEditor && !thread.raisedDecisionId && (
                <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => setDeciding(true)}>Raise as decision</Button>
              )}
            </div>
          )}
        </footer>
      )}

      {deciding && <RaiseDecisionDialog projectId={projectId} thread={thread} onClose={() => setDeciding(false)} />}
    </article>
  )
}

function CommentRow({ projectId, thread, comment, access, reply }: {
  projectId: string; thread: Thread; comment: ThreadComment; access: CommentAccess; reply: boolean
}) {
  const actions = useThreadActions(projectId, thread.draftId)
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const own = comment.author === access.userId
  const canDelete = access.canWrite && !comment.deleted && (own || access.isOwner)
  const author = comment.authorName ?? access.nameOf(comment.author)

  return (
    <li className={cn(reply && 'border-l-2 border-border pl-2')}>
      <div className="mb-0.5 flex items-baseline gap-1.5">
        <span className="min-w-0 truncate font-medium text-foreground">{author}</span>
        <time dateTime={comment.createdAt} className="shrink-0 text-[10px] text-muted-foreground">{when(comment.createdAt)}</time>
        {comment.editedAt && !comment.deleted && (
          <span className="shrink-0 text-[10px] text-muted-foreground" title={`Edited ${when(comment.editedAt)}`}>edited {when(comment.editedAt)}</span>
        )}
      </div>
      {comment.deleted ? (
        <p className="italic text-muted-foreground">Comment deleted.</p>
      ) : editing ? (
        <MentionInput
          members={access.members}
          label="Edit comment"
          placeholder="Comment"
          submitLabel="Save"
          initial={comment.body ?? ''}
          initialMentions={comment.mentions}
          autoFocus
          pending={actions.edit.isPending}
          onSubmit={(body, mentions) => actions.edit.mutate(
            { threadId: thread.id, commentId: comment.id, body, mentions },
            { onSuccess: () => setEditing(false) },
          )}
          onCancel={() => setEditing(false)}
        />
      ) : (
        <p className="whitespace-pre-wrap break-words leading-relaxed text-foreground">{comment.body}</p>
      )}
      {!editing && (canDelete || (own && access.canWrite && !comment.deleted)) && (
        <div className="mt-0.5 flex gap-2">
          {own && access.canWrite && !comment.deleted && (
            <button type="button" className="text-[11px] text-muted-foreground hover:text-foreground" onClick={() => setEditing(true)}>Edit</button>
          )}
          {canDelete && (
            <button
              type="button"
              className="text-[11px] text-muted-foreground hover:text-destructive"
              onClick={() => setDeleting(true)}
            >
              Delete
            </button>
          )}
        </div>
      )}
      {deleting && (
        <ConfirmDialog
          title="Delete comment"
          description={own
            ? 'The comment is replaced by a note that it was deleted.'
            : `Delete the comment of ${author}? It is replaced by a note that it was deleted, and the audit log records the delete.`}
          confirmLabel="Delete"
          destructive
          pending={actions.remove.isPending}
          error={actions.remove.error?.message ?? null}
          onConfirm={() => actions.remove.mutate(
            { threadId: thread.id, commentId: comment.id },
            { onSuccess: () => setDeleting(false) },
          )}
          onClose={() => { actions.remove.reset(); setDeleting(false) }}
        />
      )}
    </li>
  )
}

function RaiseDecisionDialog({ projectId, thread, onClose }: { projectId: string; thread: Thread; onClose: () => void }) {
  const actions = useThreadActions(projectId, thread.draftId)
  const opening = thread.comments[0]?.body ?? ''
  const [title, setTitle] = useState(opening.split('\n')[0].slice(0, 120))
  const [decision, setDecision] = useState('')
  const titleId = 'raise-decision-title'

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Raise as decision</DialogTitle>
          <DialogDescription>
            A proposed decision (ADR) goes to Review. Its context is the first comment and a citation of this thread. An Owner accepts or rejects it there.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault()
            actions.raiseDecision.mutate({ threadId: thread.id, title: title.trim(), decision: decision.trim() }, {
              onSuccess: (created) => {
                toast.success(`${created.number} is proposed in Review.`)
                onClose()
              },
            })
          }}
        >
          <div>
            <label htmlFor={titleId} className="mb-1 block text-xs text-muted-foreground">Title</label>
            <Input id={titleId} value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={200} />
          </div>
          <div>
            <label htmlFor={`${titleId}-decision`} className="mb-1 block text-xs text-muted-foreground">Decision</label>
            <Textarea id={`${titleId}-decision`} value={decision} onChange={(e) => setDecision(e.target.value)} required rows={4} placeholder="What is proposed to be decided" />
          </div>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={!title.trim() || !decision.trim() || actions.raiseDecision.isPending}>Propose decision</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
