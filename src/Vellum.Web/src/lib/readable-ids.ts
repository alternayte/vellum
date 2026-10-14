/**
 * Server texts (obligation recipes, the scope of a check, evidence gaps) name the parts they
 * check by ID. A reader needs the name. These functions split such a text into plain text,
 * names, and the short form of an ID whose part is gone.
 */

export type TextSegment =
  | { kind: 'text'; text: string }
  /** An ID whose part is known: the name replaces it. */
  | { kind: 'name'; text: string; id: string }
  /** An ID whose part is gone or is not a named part: show the short form and keep the full ID at hand. */
  | { kind: 'id'; text: string; id: string }

const GUID = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'

/**
 * One alternation, tried left to right at each position:
 * 1. `'Name' (ID)`: the name is already there, so the ID in brackets is dropped.
 * 2. `ID (description)`: the server put the description of the part after its ID. The
 *    description stays and the ID is dropped; the name from the model wins when there is one.
 * 3. A bare ID.
 */
const PATTERN = new RegExp(
  `('[^'\\n]*')\\s\\((${GUID})\\)` + `|(${GUID})\\s\\(([^()]*?)\\)` + `|(${GUID})`,
  'g',
)

export function shortId(id: string): string {
  return id.slice(0, 8)
}

export function readableSegments(text: string, nameOf: (id: string) => string | undefined): TextSegment[] {
  const segments: TextSegment[] = []
  const push = (segment: TextSegment) => {
    const last = segments[segments.length - 1]
    if (segment.kind === 'text' && last?.kind === 'text') last.text += segment.text
    else if (segment.text !== '') segments.push(segment)
  }
  let from = 0
  for (const match of text.matchAll(PATTERN)) {
    push({ kind: 'text', text: text.slice(from, match.index) })
    const [, quoted, , describedId, description, bareId] = match
    if (quoted !== undefined) {
      push({ kind: 'text', text: quoted })
    } else if (describedId !== undefined) {
      const name = nameOf(describedId)
      // A description that holds another ID is not a description of this part.
      if (name !== undefined) push({ kind: 'name', text: name, id: describedId })
      else if (description.trim() && !new RegExp(GUID).test(description)) push({ kind: 'name', text: description, id: describedId })
      else {
        push({ kind: 'id', text: shortId(describedId), id: describedId })
        push({ kind: 'text', text: ' (' })
        for (const inner of readableSegments(description, nameOf)) push(inner)
        push({ kind: 'text', text: ')' })
      }
      if (name !== undefined && description.trim() && description !== name) push({ kind: 'text', text: ` (${description})` })
    } else {
      const name = nameOf(bareId)
      push(name !== undefined ? { kind: 'name', text: name, id: bareId } : { kind: 'id', text: shortId(bareId), id: bareId })
    }
    from = match.index + match[0].length
  }
  push({ kind: 'text', text: text.slice(from) })
  return segments
}
