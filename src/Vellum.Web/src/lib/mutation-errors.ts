/**
 * Pass as a mutation's `onError` when the screen shows the failure next to the control
 * that caused it. It replaces the app-wide toast, which would say the same thing twice
 * and can cover the control.
 */
export function shownInline(): void {}
