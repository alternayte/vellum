import { useMemo } from 'react'
import { diffLines } from 'diff'
import { Button } from '@/components/ui/button'

interface DiffReviewProps {
  original: string
  suggested: string
  onApply: () => void
  onDismiss: () => void
}

export function DiffReview({ original, suggested, onApply, onDismiss }: DiffReviewProps) {
  const changes = useMemo(() => diffLines(original, suggested), [original, suggested])

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-4 py-2">
        <span className="text-sm font-medium">Review Suggested Changes</span>
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={onDismiss}>
            Dismiss
          </Button>
          <Button size="sm" onClick={onApply}>
            Apply All
          </Button>
        </div>
      </div>
      <div className="flex-1 overflow-y-auto p-4 font-mono text-sm">
        {changes.map((part, i) => {
          if (part.added) {
            return (
              <div key={i} className="bg-green-500/10 text-green-400">
                {part.value.split('\n').filter(Boolean).map((line, j) => (
                  <div key={j} className="px-2">+ {line}</div>
                ))}
              </div>
            )
          }
          if (part.removed) {
            return (
              <div key={i} className="bg-red-500/10 text-red-400">
                {part.value.split('\n').filter(Boolean).map((line, j) => (
                  <div key={j} className="px-2">- {line}</div>
                ))}
              </div>
            )
          }
          return (
            <div key={i} className="text-muted-foreground">
              {part.value.split('\n').filter(Boolean).map((line, j) => (
                <div key={j} className="px-2">  {line}</div>
              ))}
            </div>
          )
        })}
      </div>
    </div>
  )
}
