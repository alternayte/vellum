import { Sparkles } from 'lucide-react'
import { useShellStore } from '@/stores/shell-store'
import { ASSISTANT_TRIGGER_ID, FLYOUT_PANEL_ID } from '@/components/shell/left-toolbar'
import { cn } from '@/lib/utils'

/**
 * The button on the canvas that opens and closes the assistant. The assistant panel opens
 * next to the sidebar and keeps the canvas usable, so the button stays visible while it is open.
 */
export function AssistantCanvasButton() {
  const open = useShellStore((s) => s.activeFlyout === 'assistant')
  const toggleFlyout = useShellStore((s) => s.toggleFlyout)
  return (
    <button
      id={ASSISTANT_TRIGGER_ID}
      type="button"
      aria-expanded={open}
      aria-controls={open ? FLYOUT_PANEL_ID : undefined}
      onClick={() => toggleFlyout('assistant')}
      className={cn(
        'absolute bottom-3 left-1/2 z-10 flex h-8 -translate-x-1/2 items-center gap-1.5 rounded-full border border-border bg-card/95 px-3 text-xs shadow-md backdrop-blur hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary',
        open ? 'text-foreground' : 'text-muted-foreground hover:text-foreground',
      )}
    >
      <Sparkles className="h-3.5 w-3.5" aria-hidden="true" />
      {open ? 'Close the assistant' : 'Ask the assistant'}
    </button>
  )
}
