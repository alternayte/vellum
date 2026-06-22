import { useState } from 'react'
import { useAuth } from '@/hooks/use-auth'
import { useUpdateComment, useDeleteComment } from '@/hooks/use-draft-comments'
import type { CommentDto } from '@/hooks/use-draft-comments'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'

interface CommentListProps {
  projectId: string
  draftId: string
  comments: CommentDto[]
  entityId?: string | null
}

export function CommentList({ projectId, draftId, comments, entityId }: CommentListProps) {
  const { user } = useAuth()
  const updateComment = useUpdateComment(projectId, draftId)
  const deleteComment = useDeleteComment(projectId, draftId)

  const filtered = entityId !== undefined
    ? comments.filter((c) => c.entityId === entityId)
    : comments.filter((c) => c.entityId === null)

  if (filtered.length === 0) {
    return <p className="text-xs text-muted-foreground py-1">No comments yet.</p>
  }

  return (
    <div className="space-y-3">
      {filtered.map((comment) => (
        <CommentItem
          key={comment.id}
          comment={comment}
          isOwn={user?.email === comment.author}
          onUpdate={(body) => updateComment.mutate({ commentId: comment.id, body })}
          onDelete={() => deleteComment.mutate(comment.id)}
        />
      ))}
    </div>
  )
}

interface CommentItemProps {
  comment: CommentDto
  isOwn: boolean
  onUpdate: (body: string) => void
  onDelete: () => void
}

function CommentItem({ comment, isOwn, onUpdate, onDelete }: CommentItemProps) {
  const [editing, setEditing] = useState(false)
  const [editBody, setEditBody] = useState(comment.body)

  const handleSave = () => {
    if (editBody.trim()) {
      onUpdate(editBody.trim())
    }
    setEditing(false)
  }

  const handleCancel = () => {
    setEditBody(comment.body)
    setEditing(false)
  }

  const formattedDate = new Date(comment.createdAt).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })

  return (
    <div className="rounded border border-border bg-muted/30 p-2 text-sm">
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="font-medium text-xs truncate">{comment.author}</span>
        <span className="text-xs text-muted-foreground shrink-0">{formattedDate}</span>
      </div>

      {editing ? (
        <div className="space-y-2">
          <Textarea
            value={editBody}
            onChange={(e) => setEditBody(e.target.value)}
            rows={3}
            className="text-xs"
          />
          <div className="flex gap-1">
            <Button size="sm" className="h-6 text-xs px-2" onClick={handleSave}>
              Save
            </Button>
            <Button size="sm" variant="ghost" className="h-6 text-xs px-2" onClick={handleCancel}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <>
          <p className="text-xs whitespace-pre-wrap">{comment.body}</p>
          {isOwn && (
            <div className="mt-1 flex gap-1">
              <button
                className="text-xs text-muted-foreground hover:text-foreground"
                onClick={() => setEditing(true)}
              >
                Edit
              </button>
              <button
                className="text-xs text-muted-foreground hover:text-destructive"
                onClick={onDelete}
              >
                Delete
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
