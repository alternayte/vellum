// src/Vellum.Web/src/stores/doc-editor-store.ts
import { create } from 'zustand'

interface DocEditorState {
  activeDocId: string | null
  mode: 'edit' | 'read'
  preservedContent: Map<string, string>

  openDoc: (docId: string) => void
  closeDoc: () => void
  setMode: (mode: 'edit' | 'read') => void
  toggleMode: () => void
  setPreservedContent: (docId: string, content: string) => void
  getPreservedContent: (docId: string) => string | undefined
}

export const useDocEditorStore = create<DocEditorState>((set, get) => ({
  activeDocId: null,
  mode: 'edit',
  preservedContent: new Map(),

  openDoc: (docId) => set({ activeDocId: docId, mode: 'edit' }),
  closeDoc: () => set({ activeDocId: null }),
  setMode: (mode) => set({ mode }),
  toggleMode: () => set((state) => ({ mode: state.mode === 'edit' ? 'read' : 'edit' })),

  setPreservedContent: (docId, content) =>
    set((state) => {
      const next = new Map(state.preservedContent)
      next.set(docId, content)
      return { preservedContent: next }
    }),

  getPreservedContent: (docId) => get().preservedContent.get(docId),
}))
