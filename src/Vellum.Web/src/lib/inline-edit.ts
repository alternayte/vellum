import type { KeyboardEvent } from 'react'

/**
 * The keys of an uncontrolled field that saves when it loses focus. Enter saves: the field
 * gives up the focus, so its blur handler runs. Escape puts the saved value back before the
 * blur, so the blur handler sees no change. A multi-line field keeps Enter for a new line and
 * saves on Cmd+Enter or Ctrl+Enter.
 */
export function inlineEditKeys(saved: string, multiline = false) {
  return (e: KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    if (e.nativeEvent.isComposing) return
    if (e.key === 'Enter' && (!multiline || e.metaKey || e.ctrlKey)) {
      e.preventDefault()
      e.currentTarget.blur()
    } else if (e.key === 'Escape') {
      // Marked as used, so the workbench does not also clear the selection.
      e.preventDefault()
      e.currentTarget.value = saved
      e.currentTarget.blur()
    }
  }
}
