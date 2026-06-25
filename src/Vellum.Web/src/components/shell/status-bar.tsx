import { useCanvasStore } from '@/stores/canvas-store'
import { Button } from '@/components/ui/button'

interface StatusBarProps {
  onTidy?: () => void
  onZoomToFit?: () => void
}

export function StatusBar({ onTidy, onZoomToFit }: StatusBarProps) {
  const zoomLevel = useCanvasStore((s) => s.zoomLevel)

  return (
    <footer className="flex h-8 items-center gap-2 border-t border-border bg-card px-4">
      <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={onTidy}>
        Tidy
      </Button>
      <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={onZoomToFit}>
        Fit
      </Button>

      <div className="flex-1" />

      <span className="text-xs text-muted-foreground">
        {Math.round(zoomLevel * 100)}%
      </span>
    </footer>
  )
}
