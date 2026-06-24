import { Button } from '@/components/ui/button'
import { Trash2 } from 'lucide-react'

interface BulkActionsToolbarProps {
  selectedCount: number
  onDelete: () => void
}

export function BulkActionsToolbar({ selectedCount, onDelete }: BulkActionsToolbarProps) {
  if (selectedCount < 2) return null

  return (
    <div className="absolute left-1/2 top-3 z-20 -translate-x-1/2">
      <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5 shadow-lg">
        <span className="text-xs font-medium text-muted-foreground">
          {selectedCount} selected
        </span>
        <div className="h-4 w-px bg-border" />
        <Button
          variant="ghost"
          size="icon-sm"
          className="text-destructive hover:text-destructive"
          onClick={onDelete}
          aria-label="Delete selected"
        >
          <Trash2 className="h-3.5 w-3.5" />
        </Button>
      </div>
    </div>
  )
}
