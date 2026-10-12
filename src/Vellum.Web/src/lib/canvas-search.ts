import { factStatusLabel } from '@/lib/fact-status'

export interface SearchableNode {
  id: string
  data: {
    name?: string
    description?: string | null
    kind?: string | null
    technology?: string | null
    factStatus?: string | null
  }
}

function matchesQuery(text: string, q: string): boolean {
  // Match at word boundaries (word-start) for better precision
  const words = text.toLowerCase().split(/\W+/)
  return words.some((word) => word.startsWith(q))
}

export function filterMatchingNodes(nodes: SearchableNode[], query: string): string[] {
  if (!query.trim()) return []
  const q = query.toLowerCase()
  return nodes
    .filter((n) => !n.id.startsWith('msg-'))
    .filter((n) => {
      // Truth labels are searchable by their visible name ("inferred", "observed in source"),
      // so a reviewer can jump between every fact that is not yet declared.
      const fields = [
        n.data.name,
        n.data.description,
        n.data.kind,
        n.data.technology,
        n.data.factStatus ? factStatusLabel(n.data.factStatus) : null,
      ]
      return fields.some((field) => field != null && matchesQuery(field, q))
    })
    .map((n) => n.id)
}
