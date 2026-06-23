import { Button } from '@/components/ui/button'
import { useDraftStore } from '@/stores/draft-store'
import { useExecuteMerge } from '@/hooks/use-merge'

interface ReviewBarProps {
  projectId: string
  onMergeSuccess?: () => void
}

export function ReviewBar({ projectId, onMergeSuccess }: ReviewBarProps) {
  const { activeDraftId, reviewData, resolutions, exitReviewMode, setActiveDraft } = useDraftStore()
  const executeMerge = useExecuteMerge(projectId)

  if (!reviewData) return null

  const totalChanges = reviewData.autoResolved.length + reviewData.conflicts.length
  const conflictCount = reviewData.conflicts.length
  const resolvedCount = reviewData.conflicts.filter((c) => resolutions[c.entityId] !== undefined).length
  const allConflictsResolved = conflictCount === 0 || resolvedCount === conflictCount

  const handleMerge = async () => {
    if (!activeDraftId) return
    const resolutionList = Object.entries(resolutions).map(([entityId, resolution]) => ({
      entityId,
      resolution,
    }))
    await executeMerge.mutateAsync({
      draftId: activeDraftId,
      resolutions: resolutionList,
      expectedMainVersion: reviewData.mainVersion,
    })
    exitReviewMode()
    setActiveDraft(null, null)
    onMergeSuccess?.()
  }

  const handleCancel = () => {
    exitReviewMode()
  }

  return (
    <footer className="flex h-10 items-center gap-3 border-t border-border bg-card px-4">
      <span className="text-xs text-muted-foreground">
        {totalChanges} {totalChanges === 1 ? 'change' : 'changes'}
        {conflictCount > 0 && (
          <span className="ml-1 text-amber-500">
            · {conflictCount} {conflictCount === 1 ? 'conflict' : 'conflicts'}
            {resolvedCount > 0 && ` (${resolvedCount} resolved)`}
          </span>
        )}
      </span>

      <div className="flex-1" />

      <Button
        variant="ghost"
        size="sm"
        className="h-7 text-xs"
        onClick={handleCancel}
        disabled={executeMerge.isPending}
      >
        Cancel
      </Button>
      <Button
        size="sm"
        className="h-7 text-xs"
        onClick={handleMerge}
        disabled={!allConflictsResolved || executeMerge.isPending}
      >
        {executeMerge.isPending ? 'Merging…' : 'Merge'}
      </Button>
    </footer>
  )
}
