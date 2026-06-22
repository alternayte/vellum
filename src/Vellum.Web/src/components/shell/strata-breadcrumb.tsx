import { useCanvasStore } from '@/stores/canvas-store'
import { stratumLabel } from '@/lib/strata'

export function StrataBreadcrumb() {
  const { drillPath, drillTo } = useCanvasStore()

  return (
    <nav className="flex items-center gap-1 text-sm">
      <button
        onClick={() => drillTo(-1)}
        className={`font-display font-medium transition-colors hover:text-primary ${
          drillPath.length === 0 ? 'text-foreground' : 'text-muted-foreground'
        }`}
      >
        Context
      </button>
      {drillPath.map((segment, index) => (
        <span key={segment.elementId} className="flex items-center gap-1">
          <span className="text-muted-foreground">›</span>
          <button
            onClick={() => drillTo(index)}
            className={`font-display font-medium transition-colors hover:text-primary ${
              index === drillPath.length - 1 ? 'text-foreground' : 'text-muted-foreground'
            }`}
          >
            {stratumLabel(segment.kind, segment.name)}
          </button>
        </span>
      ))}
    </nav>
  )
}
