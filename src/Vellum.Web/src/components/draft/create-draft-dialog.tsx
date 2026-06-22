import { useState } from 'react'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Button } from '@/components/ui/button'
import { useCreateDraft } from '@/hooks/use-drafts'
import { useDraftStore } from '@/stores/draft-store'

interface CreateDraftDialogProps {
  projectId: string
  open: boolean
  onOpenChange: (open: boolean) => void
}

export function CreateDraftDialog({ projectId, open, onOpenChange }: CreateDraftDialogProps) {
  const [name, setName] = useState('')
  const createDraft = useCreateDraft(projectId)
  const { setActiveDraft } = useDraftStore()

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!name.trim()) return

    const id = crypto.randomUUID()
    const draft = await createDraft.mutateAsync({ id, name: name.trim() })
    setActiveDraft(draft.id, draft.streamId)
    setName('')
    onOpenChange(false)
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New Draft</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="flex flex-col gap-3">
          <Input
            placeholder="Draft name"
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoFocus
          />
          <DialogFooter>
            <Button
              type="submit"
              disabled={!name.trim() || createDraft.isPending}
            >
              {createDraft.isPending ? 'Creating…' : 'Create'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
