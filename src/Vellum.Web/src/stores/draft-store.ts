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
  costSheet: { changes: number; conflicts: string[] }
}

/**
 * The model the workbench shows. Main, a draft and a variant are branches: the API reads
 * and writes them with `branchId`. A revision is a stored snapshot and is read-only.
 */
export type ModelSelection =
  | { kind: 'main' }
  | { kind: 'draft'; id: string; streamId: string; name: string }
  | { kind: 'variant'; id: string; streamId: string; name: string }
  | { kind: 'revision'; id: string; name: string }

const MAIN: ModelSelection = { kind: 'main' }

interface DraftStore {
  selection: ModelSelection
  /** The ID of the selected draft. Null for every other selection. */
  activeDraftId: string | null
  /** The `branchId` of the selected draft or variant. Null for main and for a revision. */
  activeDraftStreamId: string | null
  isReviewMode: boolean
  reviewData: ReviewData | null
  resolutions: Record<string, 'keep_ours' | 'take_theirs'>

  select: (selection: ModelSelection) => void
  enterReviewMode: (data: ReviewData) => void
  /** Leaves the diff on the canvas. The selected model stays. */
  leaveReviewMode: () => void
  setResolution: (entityId: string, resolution: 'keep_ours' | 'take_theirs') => void
  clearResolution: (entityId: string) => void
  reset: () => void
}

const selected = (selection: ModelSelection) => ({
  selection,
  activeDraftId: selection.kind === 'draft' ? selection.id : null,
  activeDraftStreamId: selection.kind === 'draft' || selection.kind === 'variant' ? selection.streamId : null,
  isReviewMode: false,
  reviewData: null,
  resolutions: {},
})

export const useDraftStore = create<DraftStore>((set) => ({
  ...selected(MAIN),

  select: (selection) => set(selected(selection)),
  // The resolutions stay: a person can pick them in the merge dialog and then look at the canvas.
  enterReviewMode: (data) => set({ isReviewMode: true, reviewData: data }),
  leaveReviewMode: () => set({ isReviewMode: false, reviewData: null, resolutions: {} }),
  setResolution: (entityId, resolution) =>
    set((s) => ({ resolutions: { ...s.resolutions, [entityId]: resolution } })),
  clearResolution: (entityId) =>
    set((s) => {
      const { [entityId]: _, ...rest } = s.resolutions
      return { resolutions: rest }
    }),
  reset: () => set(selected(MAIN)),
}))

/** How the selection reads in a sentence: "main", "the draft 'x'", "the variant 'x'", "the revision 'x'". */
export function selectionName(selection: ModelSelection): string {
  return selection.kind === 'main' ? 'main' : `the ${selection.kind} '${selection.name}'`
}
