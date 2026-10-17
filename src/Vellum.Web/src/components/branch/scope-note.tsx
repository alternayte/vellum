import { scopeNote } from '@/lib/scope-note'
import type { Flyout } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'

/** The note at the top of a tool panel that says which model the panel reads. */
export function ScopeNote({ flyout }: { flyout: Flyout | 'comments' | null }) {
  const selection = useDraftStore((s) => s.selection)
  const note = scopeNote(flyout, selection)
  if (!note) return null
  return (
    <p role="note" className="border-b border-dashed border-[var(--trace-retry)]/60 px-3 py-2 text-[11px] leading-relaxed text-foreground">
      {note}
    </p>
  )
}
