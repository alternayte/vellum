import { type ReactNode, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { useShellStore, type Area } from '@/stores/shell-store'
import { Boxes, Route, ShieldCheck, PackageOpen, GitPullRequestArrow, PanelLeftClose, PanelLeftOpen } from 'lucide-react'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip'
import { areaOf, isToolVisible, visibleAreas, type AreaAccess } from '@/lib/areas'
import { cn } from '@/lib/utils'
import { PALETTE_DRAG_TYPE } from '@/lib/element-palette'

interface LeftToolbarProps {
  /** What the person sees: Check and Ship wait for the first element, Share links for an Editor. */
  access: AreaAccess
  children?: ReactNode
}

const AREA_ICONS: Record<Area, typeof Boxes> = {
  model: Boxes,
  flows: Route,
  check: ShieldCheck,
  ship: PackageOpen,
  sources: GitPullRequestArrow,
}

const AREA_WIDTHS: Record<Area, string> = {
  model: 'w-[280px]',
  flows: 'w-[280px]',
  check: 'w-[420px]',
  ship: 'w-[340px]',
  sources: 'w-[280px]',
}

/** The canvas button that opens the assistant. Focus goes back to it when the assistant closes. */
export const ASSISTANT_TRIGGER_ID = 'canvas-assistant-button'

export const FLYOUT_PANEL_ID = 'left-toolbar-flyout'

// The tooltip names the item when the sidebar shows icons only. The expanded sidebar shows
// the label itself.
function ToolTip({ label, show, children }: { label: string; show: boolean; children: ReactNode }) {
  if (!show) return children
  return (
    <Tooltip>
      <TooltipTrigger asChild>{children}</TooltipTrigger>
      <TooltipContent side="right" sideOffset={8}>{label}</TooltipContent>
    </Tooltip>
  )
}

// Below this width the expanded sidebar takes half the canvas, so it shows icons only.
const NARROW = '(max-width: 767px)'

function subscribeNarrow(onChange: () => void) {
  const query = typeof window.matchMedia === 'function' ? window.matchMedia(NARROW) : null
  query?.addEventListener('change', onChange)
  return () => query?.removeEventListener('change', onChange)
}

const isNarrow = () => typeof window.matchMedia === 'function' && window.matchMedia(NARROW).matches

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

export function LeftToolbar({ access, children }: LeftToolbarProps) {
  const { activeFlyout, openFlyout, toggleArea, closeFlyout, sidebarCollapsed, setSidebarCollapsed } = useShellStore()
  const narrow = useSyncExternalStore(subscribeNarrow, isNarrow)
  const collapsed = sidebarCollapsed || narrow
  const panel = useRef<HTMLDivElement>(null)
  // A kind dragged from the Create tab passes through the backdrop to the canvas under it, from
  // the start of the drag to its end.
  const [dragThrough, setDragThrough] = useState(false)
  useEffect(() => {
    const start = (e: DragEvent) => { if (e.dataTransfer?.types.includes(PALETTE_DRAG_TYPE)) setDragThrough(true) }
    const end = () => setDragThrough(false)
    window.addEventListener('dragstart', start)
    window.addEventListener('dragend', end)
    window.addEventListener('drop', end)
    return () => {
      window.removeEventListener('dragstart', start)
      window.removeEventListener('dragend', end)
      window.removeEventListener('drop', end)
    }
  }, [])
  const buttons = useRef(new Map<Area, HTMLButtonElement>())
  const areas = visibleAreas(access)
  const activeArea = activeFlyout ? areaOf(activeFlyout) : null
  const activeAreaDef = areas.find((a) => a.id === activeArea)
  const toolVisible = activeFlyout === null || isToolVisible(activeFlyout, access)

  // A tool whose area is hidden closes: the last element was deleted, or another model shows.
  useEffect(() => {
    if (!toolVisible) closeFlyout()
  }, [toolVisible, closeFlyout])

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
      if (!inFlyout || useShellStore.getState().activeFlyout !== null) return
      const area = areaOf(opened)
      const trigger = area ? triggers.get(area) : document.getElementById(ASSISTANT_TRIGGER_ID)
      trigger?.focus({ preventScroll: true })
    }
  }, [activeFlyout])

  const activeLabel = activeFlyout === 'assistant' ? 'Assistant' : activeAreaDef?.label

  const Toggle = collapsed ? PanelLeftOpen : PanelLeftClose

  return (
    <div className="relative z-30 flex flex-shrink-0">
      <TooltipProvider delayDuration={200}>
        <nav
          aria-label="Project tools"
          className={cn(
            'relative z-20 flex flex-col gap-1 overflow-x-hidden overflow-y-auto border-r border-border bg-card py-2 transition-[width] duration-200 ease-out',
            collapsed ? 'w-12 items-center' : 'w-48 px-2'
          )}
        >
          {areas.map(({ id, label }) => {
            const Icon = AREA_ICONS[id]
            return (
              <ToolTip key={id} label={label} show={collapsed}>
                <button
                  ref={(node) => {
                    if (node) buttons.current.set(id, node)
                    else buttons.current.delete(id)
                  }}
                  type="button"
                  aria-label={label}
                  aria-expanded={activeArea === id}
                  aria-controls={activeArea === id ? FLYOUT_PANEL_ID : undefined}
                  className={cn(
                    'flex h-9 flex-shrink-0 items-center rounded-md text-sm text-muted-foreground hover:bg-muted hover:text-foreground',
                    collapsed ? 'w-9 justify-center' : 'w-full gap-2.5 px-2.5',
                    activeArea === id && 'bg-accent text-accent-foreground'
                  )}
                  onClick={() => toggleArea(id)}
                >
                  <Icon className="h-4 w-4 flex-shrink-0" aria-hidden="true" />
                  {!collapsed && <span className="min-w-0 truncate whitespace-nowrap">{label}</span>}
                </button>
              </ToolTip>
            )
          })}
          {!narrow && (
          <div className={cn('mt-auto flex border-t border-border pt-2', collapsed ? 'w-9 justify-center' : 'w-full justify-end')}>
            {/* The toggle shows an icon only in both states, so it always has its tooltip. */}
            <ToolTip label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'} show>
              <button
                type="button"
                aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
                aria-expanded={!collapsed}
                className="flex h-9 w-9 flex-shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground"
                onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
              >
                <Toggle className="h-4 w-4" aria-hidden="true" />
              </button>
            </ToolTip>
          </div>
          )}
        </nav>
      </TooltipProvider>

      {activeFlyout && toolVisible && (
        <>
          {/* The assistant explains what is selected on the canvas or in the trace, and the review shows
              the cited parts on the canvas, so both stay open while the canvas is used. */}
          {activeFlyout !== 'assistant' && activeFlyout !== 'review' && (
            <div
              data-testid="flyout-backdrop"
              className={cn('fixed inset-0 z-10', dragThrough && 'pointer-events-none')}
              onClick={closeFlyout}
            />
          )}
          <div
            ref={panel}
            id={FLYOUT_PANEL_ID}
            role="region"
            aria-label={activeLabel ? `${activeLabel} panel` : undefined}
            tabIndex={-1}
            className={cn('absolute left-full top-0 bottom-0 z-20 flex max-w-[calc(100vw-3rem)] flex-col border-r border-border bg-card shadow-lg outline-none', activeArea ? AREA_WIDTHS[activeArea] : 'w-[280px]')}
          >
            {/* An area with more than one tool shows them as tabs. Each tab is the panel the tool had before. */}
            {activeAreaDef && activeAreaDef.tools.length > 1 && (
              <div role="tablist" aria-label={activeAreaDef.label} className="flex flex-shrink-0 gap-1 overflow-x-auto border-b border-border p-1.5">
                {activeAreaDef.tools.map((tool) => (
                  <button
                    key={tool.flyout}
                    type="button"
                    role="tab"
                    aria-selected={activeFlyout === tool.flyout}
                    className={cn(
                      'flex-shrink-0 rounded-md px-2 py-1 text-xs whitespace-nowrap text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                      activeFlyout === tool.flyout && 'bg-muted font-medium text-foreground'
                    )}
                    onClick={() => openFlyout(tool.flyout)}
                  >
                    {tool.label}
                  </button>
                ))}
              </div>
            )}
            <div className="min-h-0 flex-1">{children}</div>
          </div>
        </>
      )}
    </div>
  )
}
