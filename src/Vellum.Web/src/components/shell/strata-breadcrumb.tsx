import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { stratumLabel } from '@/lib/strata'

interface BreadcrumbDoc {
  id: string
  title: string
  spaceName?: string | null
}

interface StrataBreadcrumbProps {
  activeDoc?: BreadcrumbDoc | null
}

export function StrataBreadcrumb({ activeDoc }: StrataBreadcrumbProps = {}) {
  const { drillPath, drillTo } = useCanvasStore()
  const { activeDocId } = useShellStore()

  if (activeDocId && activeDoc) {
    return (
      <nav className="flex items-center gap-1 text-sm">
        {activeDoc.spaceName && (
          <>
            <span className="font-display font-medium text-muted-foreground">
              {activeDoc.spaceName}
            </span>
            <span className="text-muted-foreground">›</span>
          </>
        )}
        <span className="font-display font-medium text-foreground">{activeDoc.title}</span>
      </nav>
    )
  }

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
