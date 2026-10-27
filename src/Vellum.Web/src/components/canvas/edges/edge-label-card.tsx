interface EdgeLabelCardProps {
  /** The line is hidden in the view and drawn only because the person asked to see hidden lines. */
  faint?: boolean
  label: string | null
  technology: string | null
  showTechnology: boolean
  x: number
  y: number
  /** Below the point instead of centred on it: a trace label holds the space above. */
  below?: boolean
  /** The stacking order of the line, so the line passes under the label. */
  zIndex?: number
}

export function EdgeLabelCard({ label, technology, showTechnology, x, y, below = false, zIndex, faint = false }: EdgeLabelCardProps) {
  if (!label && !technology) return null

  return (
    <div
      className="nodrag nopan absolute rounded-md border border-border bg-card/95 px-2.5 py-1 shadow-sm backdrop-blur-sm"
      style={{
        transform: below ? `translate(-50%, 0) translate(${x}px, ${y + 6}px)` : `translate(-50%, -50%) translate(${x}px, ${y}px)`,
        pointerEvents: 'all',
        zIndex,
        ...(faint ? { opacity: 0.4 } : {}),
      }}
    >
      {label && (
        <span className="text-xs text-foreground">{label}</span>
      )}
      {showTechnology && technology && (
        <span className="block font-mono text-[10px] text-muted-foreground">
          {technology}
        </span>
      )}
    </div>
  )
}
