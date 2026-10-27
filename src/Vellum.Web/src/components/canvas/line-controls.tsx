import { Panel } from '@xyflow/react'
import { EyeOff } from 'lucide-react'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { LINE_SWITCHES, withDerivedLine, withRelationship, withSwitch, type LineVisibility } from '@/lib/line-visibility'

/** The line controls of the open view. */
export interface LineControls {
  visibility: LineVisibility
  /** Saves a new visibility for the view. Absent when the person cannot change the view. */
  onChange?: (visibility: LineVisibility) => void
  /** The person sees the hidden lines, faint. Not saved. */
  showHidden: boolean
  onShowHidden: (show: boolean) => void
}

interface LineControlsChipProps {
  controls: LineControls
  /** How many lines of this level the view does not draw. */
  hiddenCount: number
  elements: { id: string; name: string }[]
  relationships: { id: string; fromId: string; toId: string; label: string | null }[]
}

/**
 * The chip "N lines hidden" on the canvas. It shows whenever the view leaves a line of this level
 * out, so a hidden line never disappears without a trace, and it opens the line controls.
 */
export function LineControlsChip({ controls, hiddenCount, elements, relationships }: LineControlsChipProps) {
  if (hiddenCount === 0) return null
  const { visibility, onChange, showHidden, onShowHidden } = controls
  const nameOf = new Map(elements.map((e) => [e.id, e.name]))
  const name = (id: string) => nameOf.get(id) ?? 'a removed element'
  const byId = new Map(relationships.map((r) => [r.id, r]))
  const hiddenRelationships = visibility.hiddenRelationshipIds.flatMap((id) => {
    const r = byId.get(id)
    return r ? [r] : []
  })
  const hiddenDerived = visibility.hiddenDerivedLines.filter((l) => nameOf.has(l.fromId) && nameOf.has(l.toId))
  const text = `${hiddenCount} ${hiddenCount === 1 ? 'line' : 'lines'} hidden`

  return (
    <Panel position="bottom-left" className="vellum-line-chip max-sm:mb-16!">
      <Popover>
        <PopoverTrigger asChild>
          <button
            type="button"
            className="flex items-center gap-1.5 rounded-full border border-border bg-card/95 px-3 py-1 text-xs text-foreground shadow-md backdrop-blur hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
          >
            <EyeOff aria-hidden="true" className="h-3.5 w-3.5 text-muted-foreground" />
            {text}
            {showHidden && <span className="text-muted-foreground">· shown faint</span>}
          </button>
        </PopoverTrigger>
        <PopoverContent side="top" align="start" className="w-80 text-xs">
          <div>
            <p className="font-medium text-foreground">Lines in this view</p>
            <p className="text-muted-foreground">
              {onChange ? 'Changes apply to this view for everyone. The model keeps every relationship.' : 'This view is read-only for you.'}
            </p>
          </div>
          <fieldset className="space-y-1">
            <legend className="sr-only">Kinds of lines</legend>
            {LINE_SWITCHES.map(({ key, label }) => (
              <label key={key} className="flex items-center gap-2">
                <input
                  type="checkbox"
                  className="accent-primary"
                  checked={visibility[key]}
                  disabled={!onChange}
                  onChange={(e) => onChange?.(withSwitch(visibility, key, e.target.checked))}
                />
                {label}
              </label>
            ))}
          </fieldset>
          <label className="flex items-center gap-2 border-t border-border pt-2">
            <input type="checkbox" className="accent-primary" checked={showHidden} onChange={(e) => onShowHidden(e.target.checked)} />
            Show hidden lines <span className="text-muted-foreground">(only for you, not saved)</span>
          </label>
          {(hiddenRelationships.length > 0 || hiddenDerived.length > 0) && (
            <div className="border-t border-border pt-2">
              <p className="mb-1 text-muted-foreground">Hidden one at a time</p>
              <ul aria-label="Hidden lines" className="max-h-48 space-y-0.5 overflow-y-auto">
                {hiddenRelationships.map((r) => (
                  <HiddenItem
                    key={r.id}
                    text={`${name(r.fromId)} → ${name(r.toId)}`}
                    detail={r.label ?? 'relationship'}
                    onShow={onChange && (() => onChange(withRelationship(visibility, r.id, false)))}
                  />
                ))}
                {hiddenDerived.map((l) => (
                  <HiddenItem
                    key={`${l.fromId}>${l.toId}`}
                    text={`${name(l.fromId)} → ${name(l.toId)}`}
                    detail="derived line"
                    onShow={onChange && (() => onChange(withDerivedLine(visibility, l, false)))}
                  />
                ))}
              </ul>
            </div>
          )}
        </PopoverContent>
      </Popover>
    </Panel>
  )
}

function HiddenItem({ text, detail, onShow }: { text: string; detail: string; onShow?: () => void }) {
  return (
    <li className="flex items-center gap-2">
      <span className="min-w-0 flex-1 truncate text-foreground" title={`${text} (${detail})`}>
        {text} <span className="text-muted-foreground">· {detail}</span>
      </span>
      {onShow && (
        <button type="button" className="shrink-0 rounded px-1.5 py-0.5 text-primary hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary" onClick={onShow}>
          Show
        </button>
      )}
    </li>
  )
}
