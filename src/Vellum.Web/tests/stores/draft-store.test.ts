// src/Vellum.Web/tests/stores/draft-store.test.ts
import { describe, it, expect, beforeEach } from 'vitest'
import { useDraftStore } from '../../src/stores/draft-store'

beforeEach(() => { useDraftStore.getState().reset() })

describe('draft store', () => {
  it('setActiveDraft sets id and stream', () => {
    useDraftStore.getState().setActiveDraft('d1', 's1')
    const state = useDraftStore.getState()
    expect(state.activeDraftId).toBe('d1')
    expect(state.activeDraftStreamId).toBe('s1')
  })

  it('enterReviewMode sets review data', () => {
    const data = { autoResolved: [], conflicts: [], mainVersion: 5 }
    useDraftStore.getState().enterReviewMode(data)
    expect(useDraftStore.getState().isReviewMode).toBe(true)
    expect(useDraftStore.getState().reviewData).toEqual(data)
  })

  it('setResolution and clearResolution work', () => {
    useDraftStore.getState().setResolution('e1', 'keep_ours')
    expect(useDraftStore.getState().resolutions).toEqual({ e1: 'keep_ours' })
    useDraftStore.getState().clearResolution('e1')
    expect(useDraftStore.getState().resolutions).toEqual({})
  })
})
