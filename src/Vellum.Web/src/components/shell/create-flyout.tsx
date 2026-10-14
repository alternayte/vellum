import { useState, useEffect } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Box, LayoutDashboard, FileText, Scale } from 'lucide-react'
import { loadTemplates, type DocTemplate } from '@/lib/doc-templates'

interface CreateFlyoutProps {
  onCreateDiagram: (name: string) => void
  onCreateDoc: (title: string, content: string, type?: string) => void
  /** Opens the Add Element dialog, where the element's place and kind are chosen. */
  onAddElement?: () => void
}

type Mode = 'menu' | 'diagram' | 'doc-templates' | 'doc-title'

const FALLBACK_TEMPLATES = [
  { id: 'sdd', name: 'SDD', type: 'sdd', content: '' },
  { id: 'prd', name: 'PRD', type: 'prd', content: '' },
]

export function CreateFlyout({ onCreateDiagram, onCreateDoc, onAddElement }: CreateFlyoutProps) {
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const startDecision = useShellStore((s) => s.startDecision)
  const [mode, setMode] = useState<Mode>('menu')
  const [name, setName] = useState('')
  const [templates, setTemplates] = useState<DocTemplate[]>([])
  const [selectedTemplate, setSelectedTemplate] = useState<{ content: string; type?: string } | null>(null)

  useEffect(() => {
    loadTemplates().then(setTemplates)
  }, [])

  const handleCreateDiagram = () => {
    if (!name.trim()) return
    onCreateDiagram(name.trim())
    closeFlyout()
  }

  const handleSelectTemplate = (template: DocTemplate | null) => {
    setSelectedTemplate(template ? { content: template.content, type: template.id } : { content: '' })
    setName(template?.name ?? '')
    setMode('doc-title')
  }

  const handleCreateDoc = () => {
    if (!name.trim()) return
    onCreateDoc(name.trim(), selectedTemplate?.content ?? '', selectedTemplate?.type)
    closeFlyout()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Create
        </span>
      </div>

      <div className="flex-1 overflow-y-auto p-2">
        {mode === 'menu' && (
          <div className="space-y-1">
            {onAddElement && (
              <button
                className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
                onClick={() => { closeFlyout(); onAddElement() }}
              >
                <Box className="h-4 w-4 text-muted-foreground" />
                New Element
              </button>
            )}
            <button
              className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
              onClick={() => { setMode('diagram'); setName('') }}
            >
              <LayoutDashboard className="h-4 w-4 text-muted-foreground" />
              New Diagram
            </button>
            <button
              className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
              onClick={() => setMode('doc-templates')}
            >
              <FileText className="h-4 w-4 text-muted-foreground" />
              New Doc
            </button>
            {/* An ADR is a Review decision: an owner accepts it, and the acceptance binds its text. */}
            <button
              className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
              onClick={startDecision}
            >
              <Scale className="h-4 w-4 text-muted-foreground" />
              New Decision (ADR)
            </button>
          </div>
        )}

        {mode === 'diagram' && (
          <form onSubmit={(e) => { e.preventDefault(); handleCreateDiagram() }}>
            <Input
              aria-label="Diagram name"
              placeholder="Diagram name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoFocus
            />
            <div className="mt-2 flex gap-2">
              <Button type="button" variant="ghost" size="sm" onClick={() => setMode('menu')}>
                Back
              </Button>
              <Button type="submit" size="sm" disabled={!name.trim()}>
                Create
              </Button>
            </div>
          </form>
        )}

        {mode === 'doc-templates' && (
          <div className="space-y-1">
            <button
              className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
              onClick={() => handleSelectTemplate(null)}
            >
              Blank
            </button>
            {(templates.length > 0 ? templates : FALLBACK_TEMPLATES).map((t) => (
              <button
                key={t.id}
                className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
                onClick={() => handleSelectTemplate(t as DocTemplate)}
              >
                {t.name.toUpperCase()}
              </button>
            ))}
            <div className="mt-2">
              <Button variant="ghost" size="sm" onClick={() => setMode('menu')}>
                Back
              </Button>
            </div>
          </div>
        )}

        {mode === 'doc-title' && (
          <form onSubmit={(e) => { e.preventDefault(); handleCreateDoc() }}>
            <Input
              aria-label="Document title"
              placeholder="Document title"
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoFocus
            />
            <div className="mt-2 flex gap-2">
              <Button type="button" variant="ghost" size="sm" onClick={() => setMode('doc-templates')}>
                Back
              </Button>
              <Button type="submit" size="sm" disabled={!name.trim()}>
                Create
              </Button>
            </div>
          </form>
        )}
      </div>
    </div>
  )
}
