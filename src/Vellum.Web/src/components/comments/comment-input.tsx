import { useState } from 'react'
import { useCreateComment } from '@/hooks/use-draft-comments'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'

interface CommentInputProps {
  projectId: string
  draftId: string
  entityId?: string | null
  entityType?: string | null
  placeholder?: string
}

export function CommentInput({
  projectId,
  draftId,
  entityId,
  entityType,
  placeholder = 'Add a comment…',
}: CommentInputProps) {
  const [body, setBody] = useState('')
  const createComment = useCreateComment(projectId, draftId)

  const handleSubmit = () => {
    const trimmed = body.trim()
    if (!trimmed) return
    createComment.mutate(
      {
        id: crypto.randomUUID(),
        body: trimmed,
        entityId: entityId ?? null,
        entityType: entityType ?? null,
      },
      {
        onSuccess: () => setBody(''),
      },
    )
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
      e.preventDefault()
      handleSubmit()
    }
  }

  return (
    <div className="space-y-2">
      <Textarea
        value={body}
        onChange={(e) => setBody(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={placeholder}
        rows={3}
        className="text-xs resize-none"
      />
      <Button
        size="sm"
        className="h-6 text-xs px-2"
        onClick={handleSubmit}
        disabled={!body.trim() || createComment.isPending}
      >
        {createComment.isPending ? 'Posting…' : 'Post'}
      </Button>
    </div>
  )
}
