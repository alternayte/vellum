import { ApiError } from '@/lib/settings-api'

/**
 * Pass as a mutation's `onError` when the screen shows the failure next to the control
 * that caused it. It replaces the app-wide toast, which would say the same thing twice
 * and can cover the control.
 */
export function shownInline(): void {}

/**
 * The messages the server gave for one field of a refused request: the field itself and
 * everything under it (`links` also takes `links.elementIds`, `faults` also `faults[0].target`).
 * A screen shows them next to the control of the field.
 */
export function fieldMessages(error: unknown, field: string): string[] {
  if (!(error instanceof ApiError)) return []
  return error.fields
    .filter((e) => e.field === field || e.field.startsWith(`${field}.`) || e.field.startsWith(`${field}[`))
    .map((e) => e.message)
}
