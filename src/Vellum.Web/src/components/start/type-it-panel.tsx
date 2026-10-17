import { useEffect, useRef, useState } from 'react'
import { Check, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { api } from '@/lib/settings-api'
import { useCanvasStore } from '@/stores/canvas-store'
import { parseTypedLine, planTypedLine, type KnownElement, type KnownRelationship, type ModelWrite } from '@/lib/type-it'

interface Props {
  projectId: string
  /** The draft the writes go to. Null for main. */
  branchId: string | null
  /** The system that takes an app or a store without `in`. */
  defaultSystem: string
  elements: KnownElement[]
  relationships: KnownRelationship[]
  /** Runs after each Enter that wrote something: the page reloads the model and lays it out. */
  onWritten: () => Promise<void>
  onClose: () => void
  /** Runs when the panel opens, changes height or closes: the canvas fits again to keep clear of it. */
  onRoomChange?: () => void
}

interface Entry {
  line: string
  error: string | null
}

async function write(projectId: string, branchId: string | null, change: ModelWrite): Promise<void> {
  const query = branchId ? `?branchId=${encodeURIComponent(branchId)}` : ''
  if (change.type === 'element') {
    await api('POST', `/api/projects/${projectId}/elements${query}`, {
      id: change.id, kind: change.kind, name: change.name, description: null, technology: null,
      ownerId: null, status: 'current', parentId: change.parentId, tags: [],
    })
  } else {
    await api('POST', `/api/projects/${projectId}/relationships${query}`, {
      id: change.id, fromId: change.fromId, toId: change.toId, label: change.label, technology: null,
    })
  }
}

/**
 * One line per fact; Enter writes each line as declared elements and relationships and lays
 * the model out. A line that fails stays in the box with its reason, so the person can fix it.
 * Works with no AI provider.
 */
export function TypeItPanel({ projectId, branchId, defaultSystem, elements, relationships, onWritten, onClose, onRoomChange }: Props) {
  const [text, setText] = useState('')
  const [entries, setEntries] = useState<Entry[]>([])
  const [busy, setBusy] = useState(false)
  const box = useRef<HTMLTextAreaElement>(null)
  const panel = useRef<HTMLElement>(null)
  useEffect(() => box.current?.focus(), [])

  // The panel covers the bottom of the canvas (lib/canvas-insets). Each change of its height
  // and its close change the free canvas, so the canvas fits again.
  const roomChange = useRef(onRoomChange)
  useEffect(() => { roomChange.current = onRoomChange })
  useEffect(() => {
    const el = panel.current
    if (!el) return
    let height = -1
    const observer = new ResizeObserver(() => {
      const next = Math.round(el.getBoundingClientRect().height)
      if (next === height) return
      height = next
      roomChange.current?.()
    })
    observer.observe(el)
    return () => {
      observer.disconnect()
      // The fit runs a frame later, after the panel has left the page.
      roomChange.current?.()
    }
  }, [])

  const submit = async () => {
    if (busy || text.trim() === '') return
    setBusy(true)
    const known = { elements: [...elements], relationships: [...relationships] }
    const done: Entry[] = []
    const kept: string[] = []
    let wrote = false
    for (const line of text.split('\n')) {
      const parsed = parseTypedLine(line)
      if (parsed === null) continue
      if (parsed.type === 'error') {
        done.push({ line, error: parsed.message })
        kept.push(line)
        continue
      }
      const plan = planTypedLine(parsed, known, { defaultSystem, newId: () => crypto.randomUUID() })
      if ('error' in plan) {
        done.push({ line, error: plan.error })
        kept.push(line)
        continue
      }
      try {
        for (const change of plan.writes) {
          await write(projectId, branchId, change)
          wrote = true
          if (change.type === 'element') known.elements.push({ id: change.id, name: change.name, kind: change.kind, parentId: change.parentId })
          else known.relationships.push({ fromId: change.fromId, toId: change.toId, label: change.label })
        }
        done.push({ line: line.trim(), error: null })
      } catch (error) {
        done.push({ line, error: (error as Error).message })
        kept.push(line)
      }
    }
    // A new element inside an element of the level on the canvas shows: that element opens in place.
    const canvas = useCanvasStore.getState()
    const parentOf = new Map(known.elements.map((e) => [e.id, e.parentId]))
    for (const element of known.elements.slice(elements.length)) {
      const parent = element.parentId
      if (parent && parentOf.get(parent) === canvas.currentRootId && !useCanvasStore.getState().expandedNodeIds.has(parent)) {
        canvas.toggleExpand(parent)
      }
    }
    setEntries((current) => [...current, ...done].slice(-8))
    setText(kept.join('\n'))
    try {
      if (wrote) await onWritten()
    } finally {
      setBusy(false)
    }
  }

  return (
    <section
      ref={panel}
      aria-labelledby="type-it-title"
      data-canvas-occluder="bottom"
      // It sits above the "Ask the assistant" button, which stays at the bottom of the canvas.
      className="absolute bottom-14 left-1/2 z-20 w-[min(36rem,calc(100%-2rem))] -translate-x-1/2 rounded-lg border border-border bg-card p-3 shadow-lg"
    >
      <div className="flex items-start justify-between gap-2">
        <div>
          <h2 id="type-it-title" className="text-sm font-semibold">Type it</h2>
          <p className="text-xs text-muted-foreground">
            One fact per line: <code className="font-mono">Web app</code>, <code className="font-mono">Web app -&gt; Orders API: places orders</code>,{' '}
            <code className="font-mono">Orders DB [store] in Shop</code>. Enter writes; Shift+Enter starts a new line.
          </p>
        </div>
        <Button type="button" variant="ghost" size="sm" className="h-7 w-7 shrink-0 p-0" aria-label="Close Type it" onClick={onClose}>
          <X className="h-4 w-4" aria-hidden="true" />
        </Button>
      </div>
      {entries.length > 0 && (
        <ul className="mt-2 space-y-0.5 text-xs" aria-live="polite">
          {entries.map((entry, i) => (
            <li key={i} className="flex items-start gap-1.5">
              {entry.error
                ? <X className="mt-0.5 h-3 w-3 shrink-0 text-destructive" aria-label="Not written" />
                : <Check className="mt-0.5 h-3 w-3 shrink-0 text-primary" aria-label="Written" />}
              <span className="min-w-0">
                <span className="font-mono">{entry.line.trim()}</span>
                {entry.error && <span className="block text-destructive">{entry.error}</span>}
              </span>
            </li>
          ))}
        </ul>
      )}
      <Textarea
        ref={box}
        aria-label="Facts, one per line"
        className="mt-2 min-h-16 font-mono text-sm"
        placeholder={'Customer [actor] -> Web app: browses\nWeb app -> Orders API: places orders'}
        value={text}
        // Read-only, not disabled, while it writes: the box keeps the focus for the next line.
        readOnly={busy}
        aria-busy={busy}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => {
          // The key is used here; the canvas shortcuts must not see it.
          e.stopPropagation()
          if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
            e.preventDefault()
            void submit()
          }
          if (e.key === 'Escape') onClose()
        }}
      />
    </section>
  )
}
