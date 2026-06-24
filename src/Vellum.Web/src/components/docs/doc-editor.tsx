// src/Vellum.Web/src/components/docs/doc-editor.tsx
import { useEffect, useRef, useCallback, useState } from 'react'
import { EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands'
import { markdown, markdownLanguage } from '@codemirror/lang-markdown'
import { oneDark } from '@codemirror/theme-one-dark'
import { useDoc, useAutoSaveDoc } from '@/hooks/use-docs'
import { useElements } from '@/hooks/use-elements'
import { useViews } from '@/hooks/use-views'
import { useDocEditorStore } from '@/stores/doc-editor-store'
import { useScoringStatus, useScores, useScore, useCreateScore } from '@/hooks/use-scores'
import { hasRubricForType } from '@/lib/doc-rubrics'
import { MdxRenderer } from './mdx-renderer'
import { DocToolbar } from './doc-toolbar'
import { ScorePanel } from './score-panel'

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

  const { data: scoringStatus } = useScoringStatus()
  const [canScore, setCanScore] = useState(false)
  const [scorePanelOpen, setScorePanelOpen] = useState(false)
  const [selectedScoreId, setSelectedScoreId] = useState<string | null>(null)

  const createScore = useCreateScore(projectId, docId)
  const { data: scoreHistory } = useScores(projectId, docId)
  const { data: selectedScore } = useScore(projectId, docId, selectedScoreId)

  useEffect(() => {
    if (scoringStatus?.available && doc?.type) {
      hasRubricForType(doc.type).then(setCanScore)
    } else {
      setCanScore(false)
    }
  }, [scoringStatus?.available, doc?.type])

  const handleScore = () => {
    createScore.mutate(undefined, {
      onSuccess: (score) => {
        setSelectedScoreId(score.id)
        setScorePanelOpen(true)
      },
    })
  }

  const contentRef = useRef<string>('')
  const currentDocIdRef = useRef<string>(docId)

  if (currentDocIdRef.current !== docId) {
    currentDocIdRef.current = docId
    contentRef.current = ''
  }

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

  const displayContent = contentRef.current || getPreservedContent(docId) || doc?.content || ''

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-4 py-2">
        <h2 className="font-display text-sm font-medium">{doc?.title ?? 'Untitled'}</h2>
      </div>
      <DocToolbar
        editorView={viewRef.current}
        mode={mode}
        onToggleMode={toggleMode}
        onScore={handleScore}
        isScoring={createScore.isPending}
        canScore={canScore}
      />

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

      <ScorePanel
        open={scorePanelOpen}
        onOpenChange={setScorePanelOpen}
        score={selectedScore ?? null}
        scoreHistory={scoreHistory ?? []}
        onSelectScore={setSelectedScoreId}
        onReviewSuggestions={() => {/* wired in Task 6 */}}
        isLoading={createScore.isPending}
      />
    </div>
  )
}
