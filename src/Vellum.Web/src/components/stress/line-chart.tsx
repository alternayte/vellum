import { useId, useMemo, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { niceTicks } from '@/lib/stress-api'

export interface ChartSeries {
  name: string
  /** A CSS colour, by convention one of the `--chart-n` tokens. */
  color: string
  values: number[]
}

interface LineChartProps {
  title: string
  /** The unit of the vertical axis, such as "per second". */
  unit: string
  /** Point i is at (i + 1) * intervalMs. */
  intervalMs: number
  series: ChartSeries[]
  /** Instants to mark on the time axis, such as the end of the arrivals. */
  marks?: { atMs: number; label: string }[]
  /** One sentence that says what the chart shows, for a reader who does not see it. */
  summary: string
}

const WIDTH = 640
const HEIGHT = 230
const PAD = { top: 28, right: 14, bottom: 34, left: 48 }

const seconds = (ms: number) => {
  const s = ms / 1000
  return s >= 100 || Number.isInteger(s) ? `${Math.round(s)} s` : `${s.toFixed(1)} s`
}

const shown = (value: number) => (Number.isInteger(value) ? value.toLocaleString('en-US') : value.toLocaleString('en-US', { maximumFractionDigits: 1 }))

/**
 * A line chart over virtual time, drawn as SVG. One vertical axis and one unit for every
 * series. The pointer or the arrow keys move a crosshair that reads every series at that
 * instant, and the same numbers are in the table under the chart.
 */
export function LineChart({ title, unit, intervalMs, series, marks = [], summary }: LineChartProps) {
  const titleId = useId()
  const summaryId = useId()
  const [at, setAt] = useState<number | null>(null)
  const points = series[0]?.values.length ?? 0
  const endMs = points * intervalMs

  const { ticks, top } = useMemo(() => {
    const peak = Math.max(0, ...series.flatMap((s) => s.values))
    const ticks = niceTicks(peak)
    return { ticks, top: ticks[ticks.length - 1] }
  }, [series])

  const plotW = WIDTH - PAD.left - PAD.right
  const plotH = HEIGHT - PAD.top - PAD.bottom
  const x = (ms: number) => PAD.left + (endMs > 0 ? (ms / endMs) * plotW : 0)
  const y = (value: number) => PAD.top + plotH - (value / top) * plotH
  const xTicks = useMemo(() => niceTicks(endMs / 1000, 6).filter((s) => s * 1000 <= endMs), [endMs])

  const move = (e: PointerEvent<SVGSVGElement>) => {
    if (points === 0) return
    const box = e.currentTarget.getBoundingClientRect()
    const px = ((e.clientX - box.left) / box.width) * WIDTH
    const ms = ((px - PAD.left) / plotW) * endMs
    setAt(Math.min(points - 1, Math.max(0, Math.round(ms / intervalMs) - 1)))
  }

  const key = (e: KeyboardEvent<SVGSVGElement>) => {
    if (points === 0) return
    const step = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0
    if (e.key === 'Escape') setAt(null)
    if (step === 0) return
    e.preventDefault()
    setAt((current) => Math.min(points - 1, Math.max(0, (current ?? (step > 0 ? -1 : points)) + step)))
  }

  const atMs = at === null ? 0 : (at + 1) * intervalMs

  return (
    <figure className="rounded-md border border-border p-3">
      <figcaption className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <span id={titleId} className="text-sm font-medium text-foreground">{title}</span>
        {series.length > 1 && (
          <ul className="flex flex-wrap gap-x-3 gap-y-0.5 text-[11px] text-muted-foreground">
            {series.map((s) => (
              <li key={s.name} className="flex items-center gap-1.5">
                <span aria-hidden className="inline-block h-0.5 w-4 rounded-full" style={{ background: s.color }} />
                {s.name}
              </li>
            ))}
          </ul>
        )}
      </figcaption>
      <p id={summaryId} className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">{summary}</p>

      <div className="relative mt-2">
        <svg
          viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
          className="block w-full touch-none rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          role="img"
          aria-labelledby={titleId}
          aria-describedby={summaryId}
          tabIndex={0}
          onPointerMove={move}
          onPointerLeave={() => setAt(null)}
          onKeyDown={key}
          onBlur={() => setAt(null)}
        >
          {ticks.map((tick) => (
            <g key={tick}>
              <line x1={PAD.left} x2={WIDTH - PAD.right} y1={y(tick)} y2={y(tick)} stroke="var(--border)" strokeWidth={1} />
              <text x={PAD.left - 6} y={y(tick) + 3.5} textAnchor="end" fontSize={10} fill="var(--muted-foreground)">{shown(tick)}</text>
            </g>
          ))}
                    <text x={4} y={10} fontSize={10} fill="var(--muted-foreground)">{unit}</text>

          {xTicks.map((tick) => (
            <text key={tick} x={x(tick * 1000)} y={HEIGHT - PAD.bottom + 14} textAnchor="middle" fontSize={10} fill="var(--muted-foreground)">{shown(tick)}</text>
          ))}
          <text x={WIDTH - PAD.right} y={HEIGHT - 4} textAnchor="end" fontSize={10} fill="var(--muted-foreground)">virtual time, s</text>

          {marks.filter((m) => m.atMs > 0 && m.atMs <= endMs).map((mark) => (
            <g key={mark.label}>
              <line x1={x(mark.atMs)} x2={x(mark.atMs)} y1={PAD.top} y2={PAD.top + plotH} stroke="var(--muted-foreground)" strokeWidth={1} strokeDasharray="3 3" />
              {/* The label sits on the side of the line that has room, above the plot. */}
              <text
                x={x(mark.atMs) + (mark.atMs < endMs / 2 ? 4 : -4)} y={PAD.top - 6}
                textAnchor={mark.atMs < endMs / 2 ? 'start' : 'end'} fontSize={10} fill="var(--muted-foreground)"
              >
                {mark.label}
              </text>
            </g>
          ))}

          {series.map((s) => (
            <polyline
              key={s.name}
              fill="none"
              stroke={s.color}
              strokeWidth={2}
              strokeLinejoin="round"
              strokeLinecap="round"
              points={s.values.map((value, i) => `${x((i + 1) * intervalMs).toFixed(1)},${y(value).toFixed(1)}`).join(' ')}
            />
          ))}

          {at !== null && (
            <g>
              <line x1={x(atMs)} x2={x(atMs)} y1={PAD.top} y2={PAD.top + plotH} stroke="var(--foreground)" strokeWidth={1} opacity={0.5} />
              {series.map((s) => (
                <circle key={s.name} cx={x(atMs)} cy={y(s.values[at])} r={4} fill={s.color} stroke="var(--popover)" strokeWidth={2} />
              ))}
            </g>
          )}
        </svg>

        {at !== null && (
          <div
            role="status"
            className="pointer-events-none absolute top-1 z-10 min-w-32 rounded-md border border-border bg-popover px-2 py-1.5 text-[11px] shadow-md"
            style={atMs > endMs / 2 ? { right: `${100 - (x(atMs) / WIDTH) * 100 + 2}%` } : { left: `${(x(atMs) / WIDTH) * 100 + 2}%` }}
          >
            <p className="text-muted-foreground">at {seconds(atMs)}</p>
            {series.map((s) => (
              <p key={s.name} className="flex items-center gap-1.5">
                <span aria-hidden className="inline-block h-0.5 w-3 rounded-full" style={{ background: s.color }} />
                <span className="font-medium text-foreground">{shown(s.values[at])}</span>
                <span className="text-muted-foreground">{series.length > 1 ? s.name : unit}</span>
              </p>
            ))}
          </div>
        )}
      </div>

      <details className="mt-1 text-[11px] text-muted-foreground">
        <summary className="cursor-pointer select-none hover:text-foreground">Show the numbers</summary>
        <div className="mt-1 max-h-48 overflow-auto rounded border border-border">
          <table className="w-full border-collapse text-right font-mono">
            <caption className="sr-only">{title}, {unit}</caption>
            <thead className="sticky top-0 bg-popover">
              <tr>
                <th scope="col" className="px-2 py-1 text-left font-medium">Time</th>
                {series.map((s) => <th key={s.name} scope="col" className="px-2 py-1 font-medium">{s.name}</th>)}
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: points }, (_, i) => (
                <tr key={i} className="border-t border-border">
                  <th scope="row" className="px-2 py-0.5 text-left font-normal">{seconds((i + 1) * intervalMs)}</th>
                  {series.map((s) => <td key={s.name} className="px-2 py-0.5 text-foreground">{shown(s.values[i])}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </figure>
  )
}
