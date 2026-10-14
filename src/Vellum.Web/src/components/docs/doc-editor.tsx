// src/Vellum.Web/src/components/docs/doc-editor.tsx
import { useEffect, useRef, useCallback, useState } from 'react'
import { EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands'
import { markdown, markdownLanguage } from '@codemirror/lang-markdown'
import { oneDark } from '@codemirror/theme-one-dark'
import { useDoc, useAutoSaveDoc, useUpdateDoc } from '@/hooks/use-docs'
import { useElements } from '@/hooks/use-elements'
import { useViews } from '@/hooks/use-views'
import { useSpaces } from '@/hooks/use-spaces'
import { useProject } from '@/hooks/use-model'
import { useDocEditorStore } from '@/stores/doc-editor-store'
import { MdxRenderer } from './mdx-renderer'
import { DocToolbar } from './doc-toolbar'
import { DocActions } from './doc-actions'

interface DocEditorProps {
  projectId: string
  docId: string
}

export function DocEditor({ projectId, docId }: DocEditorProps) {
  const editorRef = useRef<HTMLDivElement>(null)
  const viewRef = useRef<EditorView | null>(null)
  const { mode, toggleMode, setPreservedContent, getPreservedContent } = useDocEditorStore()
  // What was typed since the document was loaded. Every change goes to the store, so the
  // read view renders from it and needs no copy of its own.
  const typed = useDocEditorStore((state) => state.preservedContent.get(docId))
  const { data: doc } = useDoc(projectId, docId)
  const { data: elements } = useElements(projectId)
  const { data: views } = useViews(projectId)
  const { autoSave } = useAutoSaveDoc(projectId, docId)
  const { data: spaces } = useSpaces(projectId)
  const { data: project } = useProject(projectId)
  const canEdit = project !== undefined && project.yourRole !== 'Viewer'

  const handleChange = useCallback(
    (content: string) => {
      setPreservedContent(docId, content)
      autoSave(content)
    },
    [docId, setPreservedContent, autoSave],
  )

  // The editor is created once per document and mode. Its listener reads the newest
  // handler from this ref, which is set after each render and never during one.
  const handleChangeRef = useRef(handleChange)
  useEffect(() => {
    handleChangeRef.current = handleChange
  }, [handleChange])

  useEffect(() => {
    if (!editorRef.current || mode !== 'edit') return

    const state = EditorState.create({
      doc: getPreservedContent(docId) ?? doc?.content ?? '',
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
  }, [docId, doc?.content, mode, getPreservedContent])

  // The toolbar asks for the editor when a button is pressed, not when it renders.
  const getEditorView = useCallback(() => viewRef.current, [])

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-2 border-b border-border px-4 py-2">
        {doc && canEdit ? (
          <>
            <DocTitle key={`${doc.id}:${doc.title}`} projectId={projectId} docId={doc.id} title={doc.title} />
            <DocActions
              projectId={projectId}
              doc={doc}
              spaces={spaces ?? []}
              elements={elements ?? []}
            />
          </>
        ) : (
          <h2 className="font-display text-sm font-medium">{doc?.title ?? 'Untitled'}</h2>
        )}
      </div>
      <DocToolbar getEditorView={getEditorView} mode={mode} onToggleMode={toggleMode} />

      {mode === 'edit' ? (
        <div ref={editorRef} className="flex-1 overflow-hidden" />
      ) : (
        <div className="flex-1 overflow-y-auto p-6">
          <MdxRenderer content={typed ?? doc?.content ?? ''} elements={elements ?? []} views={views} />
        </div>
      )}
    </div>
  )
}

/** The title of the document, edited in place. Enter or leaving the field saves; Escape goes back. */
function DocTitle({ projectId, docId, title }: { projectId: string; docId: string; title: string }) {
  const update = useUpdateDoc(projectId)
  const [value, setValue] = useState(title)
  const commit = () => {
    const next = value.trim()
    if (!next) { setValue(title); return }
    if (next !== title) update.mutate({ id: docId, title: next })
  }

  return (
    <div className="flex min-w-0 flex-1 items-center gap-2">
      <h2 className="min-w-0 flex-1">
        <input
          aria-label="Document title"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === 'Enter') { e.preventDefault(); e.currentTarget.blur() }
            if (e.key === 'Escape') { e.preventDefault(); setValue(title); requestAnimationFrame(() => (e.target as HTMLInputElement).blur()) }
          }}
          className="w-full min-w-0 rounded border border-transparent bg-transparent px-1 py-0.5 font-display text-sm font-medium outline-none hover:border-border focus:border-ring"
        />
      </h2>
      {update.isPending && <span className="text-[11px] text-muted-foreground">Saving…</span>}
      {update.isError && <span role="alert" className="text-[11px] text-destructive">{update.error.message}</span>}
    </div>
  )
}
