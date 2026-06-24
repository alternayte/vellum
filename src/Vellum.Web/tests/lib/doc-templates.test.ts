import { describe, it, expect } from 'vitest'
import { loadTemplates, parseFrontmatter } from '../../src/lib/doc-templates'

describe('parseFrontmatter', () => {
  it('parses YAML frontmatter from markdown string', () => {
    const raw = `---\nname: ADR\ndescription: Architecture Decision Record\nicon: scale\n---\n\n# ADR: [Title]\n\nContent here`
    const result = parseFrontmatter(raw)
    expect(result.attributes.name).toBe('ADR')
    expect(result.attributes.description).toBe('Architecture Decision Record')
    expect(result.attributes.icon).toBe('scale')
    expect(result.body).toContain('# ADR: [Title]')
    expect(result.body).toContain('Content here')
    expect(result.body).not.toContain('---')
  })

  it('returns empty attributes for content without frontmatter', () => {
    const raw = `# Just a heading\n\nSome content`
    const result = parseFrontmatter(raw)
    expect(result.attributes).toEqual({})
    expect(result.body).toBe(raw)
  })
})

describe('loadTemplates', () => {
  it('returns an array of DocTemplate objects', async () => {
    const templates = await loadTemplates()
    expect(templates.length).toBeGreaterThanOrEqual(3)

    const adr = templates.find((t) => t.id === 'adr')
    expect(adr).toBeDefined()
    expect(adr!.name).toBe('ADR')
    expect(adr!.description).toBe('Architecture Decision Record')
    expect(adr!.icon).toBe('scale')
    expect(adr!.content).toContain('## Status')
  })
})
