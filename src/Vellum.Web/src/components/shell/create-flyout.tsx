import { createElement, useState, useEffect } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { LayoutDashboard, FileText, Scale, Cloud, Box } from 'lucide-react'
import { loadTemplates, type DocTemplate } from '@/lib/doc-templates'
import { PALETTE, PALETTE_DRAG_TYPE, type PaletteEntry } from '@/lib/element-palette'
import { defaultIconForKind, getIconComponent } from '@/lib/default-icons'
import { elementColor } from '@/lib/kind-colors'

interface CreateFlyoutProps {
  onCreateDiagram: (name: string) => void
  onCreateDoc: (title: string, content: string, type?: string) => void
  /**
   * Opens the Add Element dialog with the entry's kind, where the element's place and name are
   * chosen. Without it the tab lists no kinds: a read-only model takes no new element.
   */
  onAddElement?: (entry: PaletteEntry) => void
  /** False while the model holds no element: decisions live in Check, which shows from the first element. */
  decisions?: boolean
}

type Mode = 'menu' | 'diagram' | 'doc-templates' | 'doc-title'

export function CreateFlyout({ onCreateDiagram, onCreateDoc, onAddElement, decisions = true }: CreateFlyoutProps) {
  const closeFlyout = useShellStore((s) => s.closeFlyout)
  const startDecision = useShellStore((s) => s.startDecision)
  const [mode, setMode] = useState<Mode>('menu')
  const [name, setName] = useState('')
  // Null until the template files are loaded. A template is offered only with its text: a
  // document typed as a template but with no text would look like a lost template.
  const [templates, setTemplates] = useState<DocTemplate[] | null>(null)
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
              <section aria-label="Elements" className="mb-3">
                <h3 className="px-3 pb-1 text-[11px] font-medium text-muted-foreground">Elements</h3>
                <ul className="space-y-0.5">
                  {PALETTE.map((entry) => (
                    <li key={entry.id}>
                      <button
                        type="button"
                        draggable
                        onDragStart={(e) => {
                          e.dataTransfer.setData(PALETTE_DRAG_TYPE, entry.id)
                          e.dataTransfer.effectAllowed = 'copy'
                        }}
                        className="flex w-full cursor-grab items-start gap-3 rounded-md px-3 py-1.5 text-left hover:bg-muted active:cursor-grabbing"
                        onClick={() => { closeFlyout(); onAddElement(entry) }}
                      >
                        <PaletteIcon entry={entry} />
                        <span className="min-w-0">
                          <span className="block text-sm">{entry.name}</span>
                          <span className="block text-[11px] leading-snug text-muted-foreground">{entry.description}</span>
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
                <p className="px-3 pt-1 text-[11px] text-muted-foreground">Drag a kind onto the canvas, or click it to name it first.</p>
              </section>
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
            {decisions && <button
              className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
              onClick={startDecision}
            >
              <Scale className="h-4 w-4 text-muted-foreground" />
              New Decision (ADR)
            </button>}
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
            {templates === null && <p className="px-3 py-2 text-xs text-muted-foreground">Loading templates…</p>}
            {templates?.map((t) => (
              <button
                key={t.id}
                className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-left text-sm hover:bg-muted"
                onClick={() => handleSelectTemplate(t)}
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

function PaletteIcon({ entry }: { entry: PaletteEntry }) {
  const icon = entry.id === 'external' ? Cloud : getIconComponent(defaultIconForKind(entry.kind)) ?? Box
  return (
    <span
      className="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-md border"
      style={{ borderColor: elementColor(entry.kind, entry.tags), color: elementColor(entry.kind, entry.tags) }}
      aria-hidden="true"
    >
      {createElement(icon, { className: 'h-3.5 w-3.5' })}
    </span>
  )
}
