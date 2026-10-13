/** What a key handler reads of a key event. */
export interface ShortcutKeyEvent {
  key: string
  metaKey: boolean
  ctrlKey: boolean
  altKey: boolean
  repeat?: boolean
  defaultPrevented?: boolean
  isComposing?: boolean
  target: EventTarget | null
}

const TYPING = 'input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"], [role="combobox"], [role="searchbox"]'
// A dialog and a menu own the keys while they are open, also when the focus is on one of their buttons.
const OWNS_KEYS = '[data-slot="dialog-content"], [role="menu"], [role="listbox"]'

/** True when the key goes to a control that reads typed text or picks an option by letter. */
export function isTypingTarget(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(TYPING) !== null
}

/**
 * True when a key without Ctrl, Cmd or Alt may run a workspace shortcut (N, T, 1 to 4 and
 * the Shift letters). It may not while the user types, while a dialog is open, on a key
 * that repeats because it is held, or on a key that another handler used.
 *
 * A handler that runs a shortcut must also call `preventDefault`: a shortcut that opens a
 * dialog moves the focus to a field before the key is typed, and the letter would go in.
 */
export function isPlainShortcut(event: ShortcutKeyEvent): boolean {
  if (event.metaKey || event.ctrlKey || event.altKey) return false
  if (event.repeat || event.defaultPrevented || event.isComposing) return false
  if (isTypingTarget(event.target)) return false
  return !(event.target instanceof Element && event.target.closest(OWNS_KEYS) !== null)
}
