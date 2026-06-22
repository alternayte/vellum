// src/Vellum.Web/tests/stores/doc-editor-store.test.ts
import { describe, it, expect, beforeEach } from 'vitest'
import { useDocEditorStore } from '../../src/stores/doc-editor-store'

beforeEach(() => {
  useDocEditorStore.getState().closeDoc()
})

describe('doc-editor-store', () => {
  it('opens a doc and sets edit mode', () => {
    useDocEditorStore.getState().openDoc('doc-1')
    expect(useDocEditorStore.getState().activeDocId).toBe('doc-1')
    expect(useDocEditorStore.getState().mode).toBe('edit')
  })

  it('toggles between edit and read mode', () => {
    useDocEditorStore.getState().openDoc('doc-1')
    useDocEditorStore.getState().toggleMode()
    expect(useDocEditorStore.getState().mode).toBe('read')
    useDocEditorStore.getState().toggleMode()
    expect(useDocEditorStore.getState().mode).toBe('edit')
  })

  it('preserves content across doc switches', () => {
    useDocEditorStore.getState().setPreservedContent('doc-1', '# Hello')
    useDocEditorStore.getState().openDoc('doc-2')
    expect(useDocEditorStore.getState().getPreservedContent('doc-1')).toBe('# Hello')
  })

  it('closes doc and clears activeDocId', () => {
    useDocEditorStore.getState().openDoc('doc-1')
    useDocEditorStore.getState().closeDoc()
    expect(useDocEditorStore.getState().activeDocId).toBeNull()
  })
})
