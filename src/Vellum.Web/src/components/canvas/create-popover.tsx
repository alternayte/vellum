import { kindColor } from '@/lib/kind-colors'
import { validKindsForContext } from '@/lib/containment-rules'

interface CreatePopoverProps {
  open: boolean
  sourceKind: string | null
  position: { x: number; y: number }
  onSelect: (kind: string) => void
  onClose: () => void
}

export function CreatePopover({ open, sourceKind, position, onSelect, onClose }: CreatePopoverProps) {
  if (!open) return null

  const validKinds = validKindsForContext(sourceKind)

  if (validKinds.length === 0) return null

  return (
    <div
      className="absolute z-50"
      style={{ left: position.x, top: position.y }}
    >
      <div className="rounded-lg border border-border bg-card p-1 shadow-lg">
        {validKinds.map((kind) => (
          <button
            key={kind}
            className="flex w-full items-center gap-2 rounded px-3 py-1.5 text-sm hover:bg-muted"
            onClick={() => {
              onSelect(kind)
              onClose()
            }}
          >
            <span
              className="h-2 w-2 rounded-full"
              style={{ backgroundColor: kindColor(kind) }}
            />
            <span className="font-mono text-xs uppercase">{kind}</span>
          </button>
        ))}
      </div>
    </div>
  )
}
