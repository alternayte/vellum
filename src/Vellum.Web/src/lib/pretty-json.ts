/**
 * The indented JSON of a structured value: an object, an array, or a text that holds a JSON
 * object or array. Null for anything else, which reads as plain text.
 */
export function prettyJson(value: unknown): string | null {
  if (value !== null && typeof value === 'object') return JSON.stringify(value, null, 2)
  if (typeof value !== 'string') return null
  const text = value.trim()
  if (!(text.startsWith('{') && text.endsWith('}')) && !(text.startsWith('[') && text.endsWith(']'))) return null
  try {
    const parsed: unknown = JSON.parse(text)
    return parsed !== null && typeof parsed === 'object' ? JSON.stringify(parsed, null, 2) : null
  } catch {
    return null
  }
}
