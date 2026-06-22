// src/Vellum.Web/src/components/docs/doc-editor.tsx
import { useEffect, useRef, useCallback } from 'react'
import { EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands'
import { markdown, markdownLanguage } from '@codemirror/lang-markdown'
import { oneDark } from '@codemirror/theme-one-dark'
import { useDoc, useAutoSaveDoc } from '@/hooks/use-docs'
import { useElements } from '@/hooks/use-elements'
import { useViews } from '@/hooks/use-views'
import { useDocEditorStore } from '@/stores/doc-editor-store'
import { MdxRenderer } from './mdx-renderer'
import { Button } from '@/components/ui/button'

interface DocEditorProps {
  projectId: string
  docId: string
}

export function DocEditor({ projectId, docId }: DocEditorProps) {
  const editorRef = useRef<HTMLDivElement>(null)
  const viewRef = useRef<EditorView | null>(null)
  const { mode, toggleMode, setPreservedContent, getPreservedContent } = useDocEditorStore()
  const { data: doc } = useDoc(projectId, docId)
  const { data: elements } = useElements(projectId)
  const { data: views } = useViews(projectId)
  const { autoSave } = useAutoSaveDoc(projectId, docId)

  const contentRef = useRef<string>('')

  const handleChange = useCallback(
    (content: string) => {
      contentRef.current = content
      setPreservedContent(docId, content)
      autoSave(content)
    },
    [docId, setPreservedContent, autoSave],
  )

  const handleChangeRef = useRef(handleChange)
  handleChangeRef.current = handleChange

  useEffect(() => {
    if (!editorRef.current || mode !== 'edit') return

    const preserved = getPreservedContent(docId)
    const initialContent = preserved ?? doc?.content ?? ''
    contentRef.current = initialContent

    const state = EditorState.create({
      doc: initialContent,
      extensions: [
        keymap.of([...defaultKeymap, ...historyKeymap]),
        history(),
        markdown({ base: markdownLanguage }),
        oneDark,
        EditorView.theme({
          '&': { height: '100%', fontSize: '14px' },
          '.cm-scroller': { overflow: 'auto', fontFamily: 'var(--font-mono)' },
          '.cm-content': { padding: '16px' },
        }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            handleChangeRef.current(update.state.doc.toString())
          }
        }),
      ],
    })

    const view = new EditorView({ state, parent: editorRef.current })
    viewRef.current = view

    return () => {
      view.destroy()
      viewRef.current = null
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [docId, doc?.content, mode, getPreservedContent])

  const displayContent = contentRef.current || doc?.content || ''

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-4 py-2">
        <h2 className="font-display text-sm font-medium">{doc?.title ?? 'Untitled'}</h2>
        <Button variant="outline" size="sm" onClick={toggleMode}>
          {mode === 'edit' ? 'Read' : 'Edit'}
        </Button>
      </div>

      {mode === 'edit' ? (
        <div ref={editorRef} className="flex-1 overflow-hidden" />
      ) : (
        <div className="flex-1 overflow-y-auto p-6">
          <MdxRenderer
            content={displayContent}
            elements={elements ?? []}
            views={views ?? []}
          />
        </div>
      )}
    </div>
  )
}
