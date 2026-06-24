import type { EditorView } from '@codemirror/view'
import { Button } from '@/components/ui/button'
import {
  Bold,
  Italic,
  Heading1,
  Heading2,
  Heading3,
  Link,
  Code,
  List,
  ListOrdered,
  ListChecks,
  Minus,
} from 'lucide-react'

interface DocToolbarProps {
  editorView: EditorView | null
  mode: 'edit' | 'read'
  onToggleMode: () => void
}

function wrapSelection(view: EditorView, before: string, after: string) {
  const { from, to } = view.state.selection.main
  const selected = view.state.sliceDoc(from, to)
  view.dispatch({
    changes: { from, to, insert: `${before}${selected || 'text'}${after}` },
    selection: { anchor: from + before.length, head: from + before.length + (selected.length || 4) },
  })
  view.focus()
}

function prependLine(view: EditorView, prefix: string) {
  const { from } = view.state.selection.main
  const line = view.state.doc.lineAt(from)
  view.dispatch({
    changes: { from: line.from, to: line.from, insert: prefix },
  })
  view.focus()
}

function insertText(view: EditorView, text: string) {
  const { from } = view.state.selection.main
  view.dispatch({
    changes: { from, to: from, insert: text },
    selection: { anchor: from + text.length },
  })
  view.focus()
}

export function DocToolbar({ editorView, mode, onToggleMode }: DocToolbarProps) {
  const run = (fn: (view: EditorView) => void) => {
    if (editorView) fn(editorView)
  }

  return (
    <div className="flex items-center gap-0.5 border-b border-border px-2 py-1">
      {mode === 'edit' && (
        <>
          <ToolbarButton label="Bold" icon={<Bold className="h-3.5 w-3.5" />} onClick={() => run((v) => wrapSelection(v, '**', '**'))} />
          <ToolbarButton label="Italic" icon={<Italic className="h-3.5 w-3.5" />} onClick={() => run((v) => wrapSelection(v, '*', '*'))} />
          <Divider />
          <ToolbarButton label="Heading 1" icon={<Heading1 className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '# '))} />
          <ToolbarButton label="Heading 2" icon={<Heading2 className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '## '))} />
          <ToolbarButton label="Heading 3" icon={<Heading3 className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '### '))} />
          <Divider />
          <ToolbarButton label="Link" icon={<Link className="h-3.5 w-3.5" />} onClick={() => run((v) => wrapSelection(v, '[', '](url)'))} />
          <ToolbarButton label="Code" icon={<Code className="h-3.5 w-3.5" />} onClick={() => run((v) => wrapSelection(v, '`', '`'))} />
          <Divider />
          <ToolbarButton label="Bullet list" icon={<List className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '- '))} />
          <ToolbarButton label="Numbered list" icon={<ListOrdered className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '1. '))} />
          <ToolbarButton label="Task list" icon={<ListChecks className="h-3.5 w-3.5" />} onClick={() => run((v) => prependLine(v, '- [ ] '))} />
          <Divider />
          <ToolbarButton label="Horizontal rule" icon={<Minus className="h-3.5 w-3.5" />} onClick={() => run((v) => insertText(v, '\n---\n'))} />
        </>
      )}
      <div className="flex-1" />
      <Button variant="outline" size="sm" onClick={onToggleMode}>
        {mode === 'edit' ? 'Read' : 'Edit'}
      </Button>
    </div>
  )
}

function ToolbarButton({ label, icon, onClick }: { label: string; icon: React.ReactNode; onClick: () => void }) {
  return (
    <button
      className="rounded p-1.5 text-muted-foreground hover:bg-accent hover:text-foreground"
      onClick={onClick}
      aria-label={label}
      title={label}
    >
      {icon}
    </button>
  )
}

function Divider() {
  return <div className="mx-1 h-4 w-px bg-border" />
}
