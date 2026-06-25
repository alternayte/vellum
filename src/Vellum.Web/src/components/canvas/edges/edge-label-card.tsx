interface EdgeLabelCardProps {
  label: string | null
  technology: string | null
  showTechnology: boolean
  x: number
  y: number
}

export function EdgeLabelCard({ label, technology, showTechnology, x, y }: EdgeLabelCardProps) {
  if (!label && !technology) return null

  return (
    <div
      className="nodrag nopan absolute rounded-md border border-border bg-card/95 px-2.5 py-1 shadow-sm backdrop-blur-sm"
      style={{
        transform: `translate(-50%, -50%) translate(${x}px, ${y}px)`,
        pointerEvents: 'all',
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
