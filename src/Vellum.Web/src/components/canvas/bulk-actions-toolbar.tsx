import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Trash2 } from 'lucide-react'

interface BulkActionsToolbarProps {
  selectedCount: number
  onDelete: () => void
  onBulkStatusChange?: (status: string) => void
  onBulkAddTag?: (tag: string) => void
}

export function BulkActionsToolbar({
  selectedCount,
  onDelete,
  onBulkStatusChange,
  onBulkAddTag,
}: BulkActionsToolbarProps) {
  const [tagValue, setTagValue] = useState('')

  if (selectedCount < 2) return null

  return (
    <div className="absolute left-1/2 top-3 z-20 -translate-x-1/2">
      <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5 shadow-lg">
        <span className="text-xs font-medium text-muted-foreground">
          {selectedCount} selected
        </span>
        <div className="h-4 w-px bg-border" />
        {onBulkStatusChange && (
          <select
            aria-label="Status"
            className="h-6 rounded border border-border bg-background px-1.5 text-xs"
            defaultValue=""
            onChange={(e) => {
              if (e.target.value) {
                onBulkStatusChange(e.target.value)
                e.target.value = ''
              }
            }}
          >
            <option value="" disabled>Status…</option>
            <option value="current">Current</option>
            <option value="planned">Planned</option>
            <option value="deprecated">Deprecated</option>
          </select>
        )}
        {onBulkAddTag && (
          <input
            type="text"
            placeholder="Add tag…"
            className="h-6 w-24 rounded border border-border bg-background px-1.5 text-xs outline-none"
            value={tagValue}
            onChange={(e) => setTagValue(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.stopPropagation()
                const trimmed = tagValue.trim()
                if (trimmed) {
                  onBulkAddTag(trimmed)
                  setTagValue('')
                }
              }
            }}
          />
        )}
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
