import { describe, expect, it } from 'vitest'
import { projectErrorBlocks } from '../../src/hooks/use-model'
import { ApiError } from '../../src/lib/settings-api'

describe('projectErrorBlocks', () => {
  // The defect: a failed background refetch replaced the loaded workbench with the error page.
  it('keeps a loaded workbench when a refetch fails', () => {
    expect(projectErrorBlocks(true, new ApiError(503, 'Service unavailable'))).toBe(false)
    expect(projectErrorBlocks(true, new TypeError('Failed to fetch'))).toBe(false)
  })

  it('replaces the workbench on a refusal, and on any error before the project loads', () => {
    for (const status of [400, 403, 404]) expect(projectErrorBlocks(true, new ApiError(status, 'No'))).toBe(true)
    expect(projectErrorBlocks(false, new ApiError(503, 'Service unavailable'))).toBe(true)
    expect(projectErrorBlocks(true, null)).toBe(false)
  })
})
