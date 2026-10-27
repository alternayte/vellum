import { describe, expect, it } from 'vitest'
import { TEMPLATES, templateFiles } from '@/lib/templates'

// The chooser shows the element count without loading the model; the count must be the model's.
describe('templates', () => {
  it.each(TEMPLATES.map((t) => [t.id, t] as const))('%s is bundled with its count, scenarios and stress profile', async (_, template) => {
    const { model, scenarios, profile } = await templateFiles(template.id)
    expect((model as { elements: unknown[] }).elements).toHaveLength(template.elements)
    expect(scenarios.length).toBeGreaterThan(0)
    expect(scenarios.map((s) => s.id)).toContain(profile.scenarioId)
  })
})
