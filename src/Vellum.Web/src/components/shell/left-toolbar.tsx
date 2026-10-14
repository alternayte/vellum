import { type ReactNode, useEffect, useRef } from 'react'
import { useShellStore, type Flyout } from '@/stores/shell-store'
import { FileText, Plus, LayoutGrid, Search, Cable, Route, GitPullRequestArrow, Sparkles, ShieldCheck, PackageOpen, Gauge, Coins, ClipboardCheck, MessageSquare } from 'lucide-react'
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
  { flyout: 'import', label: 'Import from GitHub', icon: GitPullRequestArrow },
  { flyout: 'assistant', label: 'Assistant', icon: Sparkles },
  { flyout: 'promises', label: 'Promises', icon: ShieldCheck },
  { flyout: 'review', label: 'Review', icon: ClipboardCheck },
  { flyout: 'comments', label: 'Comments', icon: MessageSquare },
  { flyout: 'build', label: 'Build', icon: PackageOpen },
  { flyout: 'stress', label: 'Stress', icon: Gauge },
  { flyout: 'costs', label: 'Costs', icon: Coins },
  { flyout: 'search', label: 'Search', icon: Search },
]

const FLYOUT_PANEL_ID = 'left-toolbar-flyout'

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

export function LeftToolbar({ children }: LeftToolbarProps) {
  const { activeFlyout, toggleFlyout, closeFlyout } = useShellStore()
  const panel = useRef<HTMLDivElement>(null)
  const buttons = useRef(new Map<Flyout, HTMLButtonElement>())

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      // Escape closes one layer. A dialog, a menu, a select or an inline editor above the
      // flyout that used the key has marked it as handled; the flyout then stays.
      if (e.key !== 'Escape' || e.defaultPrevented || !activeFlyout) return
      e.preventDefault()
      closeFlyout()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [activeFlyout, closeFlyout])

  // Focus goes into the flyout when it opens, and back to its toolbar button when it closes
  // while the focus was in it. A click on the canvas that closes the flyout keeps its own focus.
  useEffect(() => {
    if (!activeFlyout) return
    const opened = activeFlyout
    const node = panel.current
    const triggers = buttons.current
    const frame = requestAnimationFrame(() => {
      if (!node || node.contains(document.activeElement)) return
      const first = node.querySelector<HTMLElement>(FOCUSABLE)
      ;(first ?? node).focus({ preventScroll: true })
    })
    return () => {
      cancelAnimationFrame(frame)
      const active = document.activeElement
      const inFlyout = node !== null && (node.contains(active) || active === document.body || active === null)
      // The panel is still in the document here when another flyout replaces this one.
      if (inFlyout && useShellStore.getState().activeFlyout === null) triggers.get(opened)?.focus({ preventScroll: true })
    }
  }, [activeFlyout])

  const activeLabel = toolbarItems.find((item) => item.flyout === activeFlyout)?.label

  return (
    <div className="relative z-30 flex flex-shrink-0">
      <nav aria-label="Project tools" className="relative z-20 flex w-12 flex-col items-center gap-1 border-r border-border bg-card py-2">
        {toolbarItems.map(({ flyout, label, icon: Icon }) => (
          <button
            key={flyout}
            ref={(node) => {
              if (node) buttons.current.set(flyout, node)
              else buttons.current.delete(flyout)
            }}
            type="button"
            aria-label={label}
            // The search button opens the command palette, which is a dialog of its own.
            aria-expanded={flyout === 'search' ? undefined : activeFlyout === flyout}
            aria-controls={activeFlyout === flyout ? FLYOUT_PANEL_ID : undefined}
            title={label}
            className={cn(
              'flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground',
              activeFlyout === flyout && 'bg-accent text-accent-foreground'
            )}
            onClick={() => toggleFlyout(flyout)}
          >
            <Icon className="h-4 w-4" aria-hidden="true" />
          </button>
        ))}
      </nav>

      {activeFlyout && (
        <>
          {/* The assistant explains what is selected on the canvas or in the trace, and the review shows
              the cited parts on the canvas, so both stay open while the canvas is used. */}
          {activeFlyout !== 'assistant' && activeFlyout !== 'review' && activeFlyout !== 'comments' && (
            <div
              data-testid="flyout-backdrop"
              className="fixed inset-0 z-10"
              onClick={closeFlyout}
            />
          )}
          <div
            ref={panel}
            id={FLYOUT_PANEL_ID}
            role="region"
            aria-label={activeLabel ? `${activeLabel} panel` : undefined}
            tabIndex={-1}
            className={cn('absolute left-12 top-0 bottom-0 z-20 border-r border-border bg-card shadow-lg outline-none', activeFlyout === 'review' ? 'w-[420px]' : activeFlyout === 'comments' ? 'w-[340px]' : 'w-[280px]')}
          >
            {children}
          </div>
        </>
      )}
    </div>
  )
}
