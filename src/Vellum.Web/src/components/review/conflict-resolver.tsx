import { Button } from '@/components/ui/button'
import { useDraftStore } from '@/stores/draft-store'
import type { MergeConflict } from '@/stores/draft-store'

interface ConflictResolverProps {
  conflict: MergeConflict
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'object') return JSON.stringify(value, null, 2)
  return String(value)
}

export function ConflictResolver({ conflict }: ConflictResolverProps) {
  const { resolutions, setResolution, clearResolution } = useDraftStore()
  const current = resolutions[conflict.entityId]

  return (
    <div className="mt-4">
      <p className="mb-3 text-xs font-medium uppercase tracking-wider text-amber-500">
        Conflict — {conflict.conflictKind}
      </p>

      <div className="grid grid-cols-2 gap-2">
        {/* Main (ours) */}
        <div
          className={`rounded border p-2 transition-colors ${
            current === 'keep_ours'
              ? 'border-emerald-500 bg-emerald-500/10'
              : 'border-border bg-muted/30'
          }`}
        >
          <p className="mb-1 text-[10px] uppercase tracking-wider text-muted-foreground">
            Main (ours)
          </p>
          <pre className="whitespace-pre-wrap break-all text-[10px] text-foreground">
            {formatValue(conflict.oursValue)}
          </pre>
        </div>

        {/* Draft (theirs) */}
        <div
          className={`rounded border p-2 transition-colors ${
            current === 'take_theirs'
              ? 'border-emerald-500 bg-emerald-500/10'
              : 'border-border bg-muted/30'
          }`}
        >
          <p className="mb-1 text-[10px] uppercase tracking-wider text-muted-foreground">
            Draft (theirs)
          </p>
          <pre className="whitespace-pre-wrap break-all text-[10px] text-foreground">
            {formatValue(conflict.theirsValue)}
          </pre>
        </div>
      </div>

      <div className="mt-2 grid grid-cols-2 gap-2">
        <Button
          variant={current === 'keep_ours' ? 'default' : 'outline'}
          size="sm"
          className="h-7 text-xs"
          onClick={() =>
            current === 'keep_ours'
              ? clearResolution(conflict.entityId)
              : setResolution(conflict.entityId, 'keep_ours')
          }
        >
          {current === 'keep_ours' ? 'Keeping ours' : 'Keep ours'}
        </Button>
        <Button
          variant={current === 'take_theirs' ? 'default' : 'outline'}
          size="sm"
          className="h-7 text-xs"
          onClick={() =>
            current === 'take_theirs'
              ? clearResolution(conflict.entityId)
              : setResolution(conflict.entityId, 'take_theirs')
          }
        >
          {current === 'take_theirs' ? 'Taking theirs' : 'Take theirs'}
        </Button>
      </div>
    </div>
  )
}
