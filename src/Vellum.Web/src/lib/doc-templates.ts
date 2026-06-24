export interface DocTemplate {
  id: string
  name: string
  description: string
  icon: string
  content: string
}

interface FrontmatterResult {
  attributes: Record<string, string>
  body: string
}

export function parseFrontmatter(raw: string): FrontmatterResult {
  const match = raw.match(/^---\n([\s\S]*?)\n---\n?([\s\S]*)$/)
  if (!match) return { attributes: {}, body: raw }

  const yamlBlock = match[1]
  const body = match[2].trim()
  const attributes: Record<string, string> = {}

  for (const line of yamlBlock.split('\n')) {
    const colonIndex = line.indexOf(':')
    if (colonIndex === -1) continue
    const key = line.slice(0, colonIndex).trim()
    const value = line.slice(colonIndex + 1).trim()
    attributes[key] = value
  }

  return { attributes, body }
}

const templateFiles = import.meta.glob('../templates/*.md', {
  query: '?raw',
  import: 'default',
}) as Record<string, () => Promise<string>>

export async function loadTemplates(): Promise<DocTemplate[]> {
  const entries = Object.entries(templateFiles)
  const templates: DocTemplate[] = []

  for (const [path, loader] of entries) {
    const raw = await loader()
    const { attributes, body } = parseFrontmatter(raw)
    const filename = path.split('/').pop()?.replace('.md', '') ?? ''

    templates.push({
      id: filename,
      name: attributes.name ?? filename,
      description: attributes.description ?? '',
      icon: attributes.icon ?? 'file-text',
      content: body,
    })
  }

  return templates.sort((a, b) => a.name.localeCompare(b.name))
}
