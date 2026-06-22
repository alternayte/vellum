// src/Vellum.Web/src/stores/draft-store.ts
import { create } from 'zustand'

export type DiffState = 'added' | 'modified' | 'removed' | 'conflict' | 'unchanged'

export interface MergeChange {
  entityType: string
  entityId: string
  changeKind: string
  resolvedValue: unknown
}

export interface MergeConflict {
  entityType: string
  entityId: string
  conflictKind: string
  baseValue: unknown
  oursValue: unknown
  theirsValue: unknown
}

export interface ReviewData {
  autoResolved: MergeChange[]
  conflicts: MergeConflict[]
  mainVersion: number
}

interface DraftStore {
  activeDraftId: string | null
  activeDraftStreamId: string | null
  isReviewMode: boolean
  reviewData: ReviewData | null
  resolutions: Record<string, 'keep_ours' | 'take_theirs'>

  setActiveDraft: (draftId: string | null, streamId?: string | null) => void
  enterReviewMode: (data: ReviewData) => void
  exitReviewMode: () => void
  setResolution: (entityId: string, resolution: 'keep_ours' | 'take_theirs') => void
  clearResolution: (entityId: string) => void
  reset: () => void
}

export const useDraftStore = create<DraftStore>((set) => ({
  activeDraftId: null,
  activeDraftStreamId: null,
  isReviewMode: false,
  reviewData: null,
  resolutions: {},

  setActiveDraft: (draftId, streamId = null) =>
    set({ activeDraftId: draftId, activeDraftStreamId: streamId, isReviewMode: false, reviewData: null, resolutions: {} }),
  enterReviewMode: (data) =>
    set({ isReviewMode: true, reviewData: data, resolutions: {} }),
  exitReviewMode: () =>
    set({ activeDraftId: null, activeDraftStreamId: null, isReviewMode: false, reviewData: null, resolutions: {} }),
  setResolution: (entityId, resolution) =>
    set((s) => ({ resolutions: { ...s.resolutions, [entityId]: resolution } })),
  clearResolution: (entityId) =>
    set((s) => {
      const { [entityId]: _, ...rest } = s.resolutions
      return { resolutions: rest }
    }),
  reset: () =>
    set({ activeDraftId: null, activeDraftStreamId: null, isReviewMode: false, reviewData: null, resolutions: {} }),
}))
