import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { useDraftStore } from '@/stores/draft-store'
import { useDraft, useAbandonDraft } from '@/hooks/use-drafts'

interface DraftPillProps {
  projectId: string
}

export function DraftPill({ projectId }: DraftPillProps) {
  const { activeDraftId, setActiveDraft } = useDraftStore()
  const { data: draft } = useDraft(projectId, activeDraftId)
  const abandonDraft = useAbandonDraft(projectId)
  const [open, setOpen] = useState(false)

  if (!activeDraftId || !draft) return null

  const handleSwitchToMain = () => {
    setActiveDraft(null)
    setOpen(false)
  }

  const handleAbandon = async () => {
    await abandonDraft.mutateAsync(activeDraftId)
    setActiveDraft(null)
    setOpen(false)
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button className="flex items-center gap-1.5 rounded-md border border-border bg-accent px-2 py-1 text-xs text-accent-foreground hover:bg-accent/80">
          <span className="max-w-32 truncate font-medium">{draft.name}</span>
          <Badge variant="secondary" className="text-[10px]">
            {draft.status}
          </Badge>
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-44 p-1" align="end">
        <button
          className="w-full rounded px-2 py-1.5 text-left text-sm hover:bg-muted"
          onClick={handleSwitchToMain}
        >
          Switch to main
        </button>
        <button
          className="w-full rounded px-2 py-1.5 text-left text-sm text-destructive hover:bg-destructive/10"
          onClick={handleAbandon}
          disabled={abandonDraft.isPending}
        >
          Abandon draft
        </button>
      </PopoverContent>
    </Popover>
  )
}
