import { type ReactNode, useEffect } from 'react'
import { useShellStore, type Flyout } from '@/stores/shell-store'
import { FileText, Plus, LayoutGrid, Search, Cable, Route } from 'lucide-react'
import { cn } from '@/lib/utils'

interface LeftToolbarProps {
  children?: ReactNode
}

const toolbarItems: { flyout: Flyout; label: string; icon: typeof FileText }[] = [
  { flyout: 'files', label: 'Project files', icon: FileText },
  { flyout: 'create', label: 'Create new', icon: Plus },
  { flyout: 'catalog', label: 'Element catalog', icon: LayoutGrid },
  { flyout: 'contracts', label: 'Contracts', icon: Cable },
  { flyout: 'scenarios', label: 'Scenarios', icon: Route },
  { flyout: 'search', label: 'Search', icon: Search },
]

export function LeftToolbar({ children }: LeftToolbarProps) {
  const { activeFlyout, toggleFlyout, closeFlyout } = useShellStore()

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && activeFlyout) {
        closeFlyout()
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [activeFlyout, closeFlyout])

  return (
    <div className="relative z-30 flex flex-shrink-0">
      <nav className="flex w-12 flex-col items-center gap-1 border-r border-border bg-card py-2">
        {toolbarItems.map(({ flyout, label, icon: Icon }) => (
          <button
            key={flyout}
            aria-label={label}
            title={label}
            className={cn(
              'flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground',
              activeFlyout === flyout && 'bg-accent text-accent-foreground'
            )}
            onClick={() => toggleFlyout(flyout)}
          >
            <Icon className="h-4 w-4" />
          </button>
        ))}
      </nav>

      {activeFlyout && (
        <>
          <div
            data-testid="flyout-backdrop"
            className="fixed inset-0 z-10"
            onClick={closeFlyout}
          />
          <div className="absolute left-12 top-0 bottom-0 z-20 w-[280px] border-r border-border bg-card shadow-lg">
            {children}
          </div>
        </>
      )}
    </div>
  )
}
