import { describe, it, expect } from 'vitest'
import { loadRubrics, hasRubricForType } from '../../src/lib/doc-rubrics'

describe('loadRubrics', () => {
  it('loads all rubric files and extracts metadata', async () => {
    const rubrics = await loadRubrics()
    expect(rubrics.length).toBeGreaterThanOrEqual(3)

    const adr = rubrics.find(r => r.docType === 'adr')
    expect(adr).toBeDefined()
    expect(adr!.name).toBe('ADR Quality Rubric')
    expect(adr!.id).toBe('adr')
  })
})

describe('hasRubricForType', () => {
  it('returns true for adr', async () => {
    expect(await hasRubricForType('adr')).toBe(true)
  })

  it('returns false for unknown type', async () => {
    expect(await hasRubricForType('unknown')).toBe(false)
  })
})
