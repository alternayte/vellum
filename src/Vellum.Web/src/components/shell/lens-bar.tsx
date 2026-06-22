import { useCanvasStore } from '@/stores/canvas-store'
import { Button } from '@/components/ui/button'

interface LensBarProps {
  onTidy?: () => void
  onZoomToFit?: () => void
}

export function LensBar({ onTidy, onZoomToFit }: LensBarProps) {
  const { activeLens, toggleLens } = useCanvasStore()

  return (
    <footer className="flex h-10 items-center gap-2 border-t border-border bg-card px-4">
      <Button
        variant={activeLens === 'status' ? 'default' : 'ghost'}
        size="sm"
        className="h-7 text-xs"
        onClick={() => toggleLens('status')}
      >
        Status
      </Button>
      <Button
        variant={activeLens === 'metadata' ? 'default' : 'ghost'}
        size="sm"
        className="h-7 text-xs"
        onClick={() => toggleLens('metadata')}
      >
        Metadata
      </Button>

      <div className="flex-1" />

      <Button variant="ghost" size="sm" className="h-7 text-xs" onClick={onTidy}>
        Tidy
      </Button>
      <Button variant="ghost" size="sm" className="h-7 text-xs" onClick={onZoomToFit}>
        Fit
      </Button>
    </footer>
  )
}
