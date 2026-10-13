import { Button } from '@/components/ui/button'
import { useDraftStore } from '@/stores/draft-store'
import { useExecuteMerge } from '@/hooks/use-merge'

interface ReviewBarProps {
  projectId: string
  onMergeSuccess?: () => void
}

export function ReviewBar({ projectId, onMergeSuccess }: ReviewBarProps) {
  const { activeDraftId, reviewData, resolutions, leaveReviewMode, select } = useDraftStore()
  const executeMerge = useExecuteMerge(projectId)

  if (!reviewData) return null

  const totalChanges = reviewData.autoResolved.length + reviewData.conflicts.length
  const conflictCount = reviewData.conflicts.length
  const resolvedCount = reviewData.conflicts.filter((c) => resolutions[c.entityId] !== undefined).length
  const allConflictsResolved = conflictCount === 0 || resolvedCount === conflictCount
  // A conflict between the two cost sheets has no resolution to pick here. The server refuses the merge.
  const costSheet = reviewData.costSheet
  const sheetConflicts = costSheet.conflicts

  const handleMerge = async () => {
    if (!activeDraftId) return
    const resolutionList = Object.entries(resolutions).map(([entityId, resolution]) => ({
      entityId,
      resolution,
    }))
    try {
      await executeMerge.mutateAsync({
        draftId: activeDraftId,
        resolutions: resolutionList,
        expectedMainVersion: reviewData.mainVersion,
      })
    } catch {
      // Nothing was merged. The draft stays open and the reason is shown in the bar.
      return
    }
    select({ kind: 'main' })
    onMergeSuccess?.()
  }

  const handleCancel = () => {
    leaveReviewMode()
  }

  return (
    <footer className="flex min-h-10 flex-wrap items-center gap-x-3 gap-y-1 border-t border-border bg-card px-4 py-1.5">
      <span className="text-xs text-muted-foreground">
        {totalChanges} {totalChanges === 1 ? 'change' : 'changes'}
        {conflictCount > 0 && (
          <span className="ml-1 text-amber-500">
            · {conflictCount} {conflictCount === 1 ? 'conflict' : 'conflicts'}
            {resolvedCount > 0 && ` (${resolvedCount} resolved)`}
          </span>
        )}
        {costSheet.changes > 0 && ` · ${costSheet.changes} cost sheet ${costSheet.changes === 1 ? 'change' : 'changes'}`}
      </span>
      {sheetConflicts.length > 0 && (
        <span role="alert" className="text-xs text-amber-500">
          The cost sheets do not merge: {sheetConflicts.join('; ')}. Change the cost sheet of the draft or of main until the two agree.
        </span>
      )}
      {executeMerge.isError && <span role="alert" className="text-xs text-destructive">{executeMerge.error.message}</span>}

      <div className="flex-1" />

      <Button
        variant="ghost"
        size="sm"
        className="h-7 text-xs"
        onClick={handleCancel}
        disabled={executeMerge.isPending}
      >
        Close the diff
      </Button>
      <Button
        size="sm"
        className="h-7 text-xs"
        onClick={handleMerge}
        disabled={!allConflictsResolved || sheetConflicts.length > 0 || executeMerge.isPending}
      >
        {executeMerge.isPending ? 'Merging…' : 'Merge'}
      </Button>
    </footer>
  )
}
