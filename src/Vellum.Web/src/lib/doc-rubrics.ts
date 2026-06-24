export interface RubricMeta {
  id: string
  name: string
  docType: string
}

const rubricFiles = import.meta.glob('../rubrics/*.md', {
  query: '?raw',
  import: 'default',
}) as Record<string, () => Promise<string>>

function parseTopLevelFrontmatter(raw: string): Record<string, string> {
  const match = raw.match(/^---\n([\s\S]*?)\n---/)
  if (!match) return {}

  const attributes: Record<string, string> = {}
  for (const line of match[1].split('\n')) {
    if (/^\s/.test(line)) continue // skip indented lines (nested YAML)
    const colonIndex = line.indexOf(':')
    if (colonIndex === -1) continue
    const key = line.slice(0, colonIndex).trim()
    const value = line.slice(colonIndex + 1).trim()
    attributes[key] = value
  }
  return attributes
}

export async function loadRubrics(): Promise<RubricMeta[]> {
  const entries = Object.entries(rubricFiles)
  const rubrics: RubricMeta[] = []

  for (const [path, loader] of entries) {
    const raw = await loader()
    const attributes = parseTopLevelFrontmatter(raw)
    const filename = path.split('/').pop()?.replace('.md', '') ?? ''

    rubrics.push({
      id: filename,
      name: attributes.name ?? filename,
      docType: attributes.docType ?? filename,
    })
  }

  return rubrics.sort((a, b) => a.name.localeCompare(b.name))
}

let rubricCache: RubricMeta[] | null = null

export async function hasRubricForType(type: string): Promise<boolean> {
  if (!rubricCache) rubricCache = await loadRubrics()
  return rubricCache.some(r => r.docType === type)
}
