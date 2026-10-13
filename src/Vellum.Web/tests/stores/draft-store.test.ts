// src/Vellum.Web/tests/stores/draft-store.test.ts
import { describe, it, expect, beforeEach } from 'vitest'
import { useDraftStore } from '../../src/stores/draft-store'

beforeEach(() => { useDraftStore.getState().reset() })

describe('draft store', () => {
  it('a draft gives its ID and its stream as the branch', () => {
    useDraftStore.getState().select({ kind: 'draft', id: 'd1', streamId: 's1', name: 'outbox' })
    const state = useDraftStore.getState()
    expect(state.activeDraftId).toBe('d1')
    expect(state.activeDraftStreamId).toBe('s1')
  })

  it('a variant is a branch and no draft', () => {
    useDraftStore.getState().select({ kind: 'variant', id: 'v1', streamId: 's2', name: 'queue' })
    const state = useDraftStore.getState()
    expect(state.activeDraftId).toBeNull()
    expect(state.activeDraftStreamId).toBe('s2')
  })

  it('a revision is no branch, so no request names one', () => {
    useDraftStore.getState().select({ kind: 'draft', id: 'd1', streamId: 's1', name: 'outbox' })
    useDraftStore.getState().select({ kind: 'revision', id: 'r1', name: 'before' })
    const state = useDraftStore.getState()
    expect(state.activeDraftId).toBeNull()
    expect(state.activeDraftStreamId).toBeNull()
  })

  it('a new selection leaves the diff of the last one', () => {
    useDraftStore.getState().enterReviewMode({ autoResolved: [], conflicts: [], mainVersion: 5, costSheet: { changes: 0, conflicts: [] } })
    useDraftStore.getState().setResolution('e1', 'keep_ours')
    useDraftStore.getState().select({ kind: 'main' })
    const state = useDraftStore.getState()
    expect(state.isReviewMode).toBe(false)
    expect(state.resolutions).toEqual({})
  })

  it('enterReviewMode sets review data', () => {
    const data = { autoResolved: [], conflicts: [], mainVersion: 5, costSheet: { changes: 0, conflicts: [] } }
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
