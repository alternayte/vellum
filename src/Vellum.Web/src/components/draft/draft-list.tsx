import { useState } from 'react'
import { useDrafts } from '@/hooks/use-drafts'
import { useDraftStore } from '@/stores/draft-store'
import { CreateDraftDialog } from './create-draft-dialog'

interface DraftListProps {
  projectId: string
}

export function DraftList({ projectId }: DraftListProps) {
  const { data } = useDrafts(projectId, 'open')
  const { activeDraftId, setActiveDraft } = useDraftStore()
  const [dialogOpen, setDialogOpen] = useState(false)

  const drafts = data?.items ?? []

  return (
    <div className="border-t border-border">
      <div className="flex items-center justify-between px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Drafts
        </span>
        <button
          className="text-xs text-primary hover:text-primary/80"
          onClick={() => setDialogOpen(true)}
        >
          +
        </button>
      </div>
      <div className="px-2 pb-2">
        {drafts.length === 0 && (
          <p className="px-2 py-1 text-xs text-muted-foreground">No open drafts</p>
        )}
        {drafts.map((draft) => (
          <button
            key={draft.id}
            className={`w-full rounded px-2 py-1 text-left text-sm hover:bg-muted ${
              activeDraftId === draft.id ? 'bg-accent text-accent-foreground' : ''
            }`}
            onClick={() => setActiveDraft(draft.id, draft.streamId)}
          >
            <span className="truncate">{draft.name}</span>
          </button>
        ))}
      </div>

      <CreateDraftDialog
        projectId={projectId}
        open={dialogOpen}
        onOpenChange={setDialogOpen}
      />
    </div>
  )
}
