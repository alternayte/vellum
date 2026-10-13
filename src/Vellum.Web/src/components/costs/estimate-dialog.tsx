import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import {
  FLAG_LABEL, NOT_COMPARABLE, UNKNOWN_REASON, costsApi, describeError, formatAmount, formatMoney, formatNumber, isEmptySide, priceFor, priceLabel,
  type AmountDelta, type Branch, type Estimate, type SavedEstimate, type UnknownLine,
} from '@/lib/costs-api'

interface Props {
  projectId: string
  branchId: string | null
  branches: Branch[]
  onClose: () => void
}

type Tab = 'lines' | 'saved' | 'compare'

const headingClass = 'text-xs font-medium uppercase tracking-wider text-muted-foreground'
const cell = 'px-2.5 py-1.5 align-top'
const numeric = `${cell} text-right font-mono tabular-nums`

function Totals({ estimate }: { estimate: Estimate }) {
  return (
    <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
      {estimate.totals.length === 0 && <span className="text-sm text-foreground">No known cost</span>}
      {estimate.totals.map((total) => <span key={total.currency} className="font-mono text-lg tabular-nums text-foreground">{formatMoney(total)}</span>)}
      <span className="text-xs text-muted-foreground">for one month, known lines only, as of {estimate.asOf}</span>
    </div>
  )
}

function UnknownTable({ lines, title }: { lines: UnknownLine[]; title: string }) {
  if (lines.length === 0) return null
  return (
    <section>
      <h3 className={headingClass}>{title} <span className="font-mono">{lines.length}</span></h3>
      <p className="mt-1 text-xs text-muted-foreground">These lines have no amount. They are in no subtotal and in no total.</p>
      <div className="mt-1.5 overflow-x-auto rounded-md border border-dashed border-border">
        <table className="w-full text-left text-xs">
          <thead className="border-b border-border text-muted-foreground">
            <tr><th className={`${cell} font-medium`}>Resource</th><th className={`${cell} font-medium`}>Line</th><th className={`${cell} font-medium`}>Reason</th><th className={`${cell} font-medium`}>Detail</th></tr>
          </thead>
          <tbody>
            {lines.map((line) => (
              <tr key={line.lineId} className="border-b border-border last:border-b-0">
                <td className={cell}>{line.resourceName}</td>
                <td className={cell}>{line.name}</td>
                <td className={`${cell} whitespace-nowrap text-[var(--trace-retry)]`}>{UNKNOWN_REASON[line.reason] ?? line.reason}</td>
                <td className={`${cell} text-muted-foreground`}>{line.detail}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

/** Every line of an estimate: the quantity and the price with their labels, the conversion and the amount. */
function EstimateView({ estimate, hash }: { estimate: Estimate; hash: string }) {
  const known = estimate.lines.filter((l) => l.status === 'known')
  return (
    <div className="space-y-4">
      <Totals estimate={estimate} />

      {estimate.categories.length > 0 && (
        <section>
          <h3 className={headingClass}>By category</h3>
          <ul className="mt-1.5 grid gap-1.5 sm:grid-cols-3">
            {estimate.categories.map((c) => (
              <li key={c.category} className="rounded-md border border-border px-2.5 py-1.5">
                <p className="text-xs text-muted-foreground">{c.category}</p>
                <p className="font-mono text-sm tabular-nums text-foreground">{c.known.length > 0 ? c.known.map(formatMoney).join(' + ') : 'no known cost'}</p>
                {c.unknownLines > 0 && <p className="text-[11px] text-[var(--trace-retry)]">{c.unknownLines} unknown</p>}
              </li>
            ))}
          </ul>
        </section>
      )}

      {known.length > 0 && (
        <section>
          <h3 className={headingClass}>Known lines <span className="font-mono">{known.length}</span></h3>
          <div className="mt-1.5 overflow-x-auto rounded-md border border-border">
            <table className="w-full text-left text-xs">
              <thead className="border-b border-border text-muted-foreground">
                <tr>
                  <th className={`${cell} font-medium`}>Resource</th><th className={`${cell} font-medium`}>Line</th>
                  <th className={`${cell} text-right font-medium`}>Quantity</th><th className={`${cell} text-right font-medium`}>Unit price</th>
                  <th className={`${cell} font-medium`}>Conversion</th><th className={`${cell} text-right font-medium`}>Amount</th>
                </tr>
              </thead>
              <tbody>
                {known.map((line) => (
                  <tr key={line.id} className="border-b border-border last:border-b-0">
                    <td className={cell}>{line.resourceName}<span className="block text-[11px] text-muted-foreground">{line.category}</span></td>
                    <td className={cell}>{line.name}</td>
                    <td className={numeric}>
                      {formatNumber(line.quantity!.value)} {line.quantity!.unit}
                      <span className="block font-sans text-[11px] text-muted-foreground">{line.quantity!.label}</span>
                    </td>
                    <td className={numeric}>
                      {formatNumber(line.price!.unitPrice)} {line.price!.currency} per {line.price!.unit}
                      <span className="block font-sans text-[11px] text-muted-foreground">{priceFor(line.price!)}</span>
                      <span className="block font-sans text-[11px] text-muted-foreground">{priceLabel(line.price!)}</span>
                    </td>
                    <td className={`${cell} text-muted-foreground`}>{line.conversion}</td>
                    <td className={numeric}>
                      {formatAmount(line.amount!)} {line.currency}
                      {line.flags.map((flag) => <span key={flag} className="block font-sans text-[11px] text-[var(--trace-retry)]">{FLAG_LABEL[flag] ?? flag}</span>)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      <UnknownTable lines={estimate.unknown} title="Unknown" />

      <p className="font-mono text-[10px] text-muted-foreground">
        engine {estimate.engine} · a price is stale after {estimate.staleAfterDays} days · sha256 {hash}
      </p>
    </div>
  )
}

function SavedRow({ projectId, branchId, saved, onRemoved }: { projectId: string; branchId: string | null; saved: SavedEstimate; onRemoved: () => void }) {
  const [open, setOpen] = useState(false)
  const detail = useQuery({
    queryKey: ['projects', projectId, 'costs', branchId, 'saved', saved.id],
    queryFn: () => costsApi.savedOne(projectId, branchId, saved.id),
    enabled: open,
  })
  const remove = useMutation({ mutationFn: () => costsApi.removeSaved(projectId, branchId, saved.id), onSuccess: onRemoved, onError: shownInline })

  return (
    <li className="rounded-md border border-border p-3">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="text-sm font-medium text-foreground">{saved.name}</span>
        <span className="text-xs text-muted-foreground">as of {saved.asOf} · saved {new Date(saved.savedAt).toLocaleString()} · model version {saved.modelVersion}</span>
        <span className={cn('rounded-full border px-1.5 text-[11px]', saved.outdated ? 'border-[var(--trace-retry)] text-[var(--trace-retry)]' : 'border-border text-muted-foreground')}>
          {saved.outdated ? 'outdated' : 'inputs unchanged'}
        </span>
        <span className="ml-auto flex gap-1">
          <Button size="sm" variant="ghost" onClick={() => setOpen(!open)}>{open ? 'Hide' : 'Show'}</Button>
          <Button size="sm" variant="ghost" disabled={remove.isPending} onClick={() => remove.mutate()}>Delete</Button>
        </span>
      </div>
      {saved.outdated && (
        <ul className="mt-1.5 text-xs text-muted-foreground">
          {saved.changes.map((change, i) => <li key={i}>{change.kind} <span className="text-foreground">{change.name}</span> {change.change}</li>)}
        </ul>
      )}
      {remove.isError && <p role="alert" className="mt-1 text-xs text-destructive">{describeError(remove.error)}</p>}
      {open && detail.data?.result && <div className="mt-3 border-t border-border pt-3"><EstimateView estimate={detail.data.result} hash={saved.hash} /></div>}
    </li>
  )
}

function DeltaCells({ amounts }: { amounts: AmountDelta[] }) {
  const show = (value: number | null, currency: string) => (value === null ? '—' : `${formatAmount(value)} ${currency}`)
  if (amounts.length === 0) return <><td className={numeric}>—</td><td className={numeric}>—</td><td className={numeric}>—</td></>
  return (
    <>
      <td className={numeric}>{amounts.map((a) => <span key={a.currency} className="block">{show(a.left, a.currency)}</span>)}</td>
      <td className={numeric}>{amounts.map((a) => <span key={a.currency} className="block">{show(a.right, a.currency)}</span>)}</td>
      <td className={numeric}>
        {amounts.map((a) => (
          <span key={a.currency} className="block">
            {a.delta === null ? <span className="font-sans text-[var(--trace-retry)]">not known</span> : `${a.delta > 0 ? '+' : ''}${formatAmount(a.delta)} ${a.currency}`}
          </span>
        ))}
      </td>
    </>
  )
}

function CompareView({ projectId, branchId, branches }: { projectId: string; branchId: string | null; branches: Branch[] }) {
  const [left, setLeft] = useState<string | null>(null)
  const [right, setRight] = useState<string | null>(branchId ?? branches.find((b) => b.id !== null)?.id ?? null)
  const result = useQuery({
    queryKey: ['projects', projectId, 'costs', 'compare', left, right],
    queryFn: () => costsApi.compare(projectId, left, right),
    enabled: left !== right,
    staleTime: 0,
  })
  const label = (id: string | null) => branches.find((b) => b.id === id)?.label ?? 'main'
  const comparison = result.data?.comparison
  const side = (name: string, value: string | null, onChange: (value: string | null) => void) => (
    <label className="block text-xs font-medium text-muted-foreground">
      {name}
      <select className={`${selectClass} mt-1`} value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
        {branches.map((b) => <option key={b.id ?? ''} value={b.id ?? ''}>{b.label}</option>)}
      </select>
    </label>
  )

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        {side('Left', left, setLeft)}
        {side('Right', right, setRight)}
      </div>
      {branches.length < 2 && <p className="text-xs text-muted-foreground">The project has one branch. Create a variant or a draft to compare it with main.</p>}
      {branches.length >= 2 && left === right && <p className="text-xs text-muted-foreground">Choose two different branches.</p>}
      {result.isError && <p role="alert" className="text-xs text-destructive">{describeError(result.error)}</p>}

      {comparison && (
        <>
          <div className="rounded-md border border-border p-3">
            {comparison.comparable ? (
              <p className="text-sm text-foreground">
                {comparison.cheaper === 'equal'
                  ? 'Both sides cost the same.'
                  : <>{label(comparison.cheaper === 'left' ? left : right)} costs <span className="font-mono tabular-nums">{formatMoney(comparison.difference!)}</span> less for one month.</>}
              </p>
            ) : (
              <>
                <p className="text-sm text-[var(--trace-retry)]">Not comparable. {NOT_COMPARABLE[comparison.reason!] ?? comparison.reason}</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  {isEmptySide(comparison.reason)
                    ? 'An empty cost sheet is not a cost of zero. Vellum does not say which side costs less and shows no difference. Add resources to the empty side, or copy a sheet into it.'
                    : 'Vellum does not say which side costs less while a cost is unknown. The differences below are for complete rows only.'}
                </p>
              </>
            )}
          </div>

          <div className="overflow-x-auto rounded-md border border-border">
            <table className="w-full text-left text-xs">
              <thead className="border-b border-border text-muted-foreground">
                <tr>
                  <th className={`${cell} font-medium`}></th>
                  <th className={`${cell} text-right font-medium`}>{label(left)}</th><th className={`${cell} text-right font-medium`}>{label(right)}</th>
                  <th className={`${cell} text-right font-medium`}>Difference</th>
                </tr>
              </thead>
              <tbody>
                <tr className="border-b border-border font-medium"><td className={cell}>Total, known lines</td><DeltaCells amounts={comparison.totals} /></tr>
                {comparison.categories.map((c) => (
                  <tr key={c.category} className="border-b border-border text-muted-foreground">
                    <td className={cell}>{c.category}{c.leftUnknownLines + c.rightUnknownLines > 0 && <span className="ml-1.5 text-[var(--trace-retry)]">{c.leftUnknownLines + c.rightUnknownLines} unknown</span>}</td>
                    <DeltaCells amounts={c.amounts} />
                  </tr>
                ))}
                {comparison.resources.map((r) => (
                  <tr key={`${r.elementId}/${r.name}`} className="border-b border-border last:border-b-0">
                    <td className={cell}>
                      {r.name}
                      <span className="ml-1.5 rounded-full border border-border px-1.5 text-[11px] text-muted-foreground">{r.change}</span>
                      {r.leftUnknownLines + r.rightUnknownLines > 0 && <span className="ml-1.5 text-[11px] text-[var(--trace-retry)]">{r.leftUnknownLines + r.rightUnknownLines} unknown</span>}
                    </td>
                    <DeltaCells amounts={r.amounts} />
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <UnknownTable lines={comparison.leftUnknown} title={`Unknown in ${label(left)}`} />
          <UnknownTable lines={comparison.rightUnknown} title={`Unknown in ${label(right)}`} />
        </>
      )}
    </div>
  )
}

/** The estimate of a branch line by line, the estimates a person saved, and the comparison of two branches. */
export function EstimateDialog({ projectId, branchId, branches, onClose }: Props) {
  const queryClient = useQueryClient()
  const [tab, setTab] = useState<Tab>('lines')
  const [name, setName] = useState('')
  const live = useQuery({ queryKey: ['projects', projectId, 'costs', branchId, 'estimate'], queryFn: () => costsApi.estimate(projectId, branchId), staleTime: 0 })
  const saved = useQuery({ queryKey: ['projects', projectId, 'costs', branchId, 'saved'], queryFn: () => costsApi.saved(projectId, branchId) })
  const refreshSaved = () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'costs', branchId, 'saved'] })
  const save = useMutation({
    mutationFn: () => costsApi.save(projectId, branchId, name.trim()),
    onSuccess: () => { setName(''); setTab('saved'); refreshSaved() },
    onError: shownInline,
  })
  const branchLabel = branches.find((b) => b.id === branchId)?.label ?? 'main'
  const tabs: { id: Tab; label: string }[] = [
    { id: 'lines', label: 'Estimate' },
    { id: 'saved', label: `Saved${saved.data ? ` ${saved.data.length}` : ''}` },
    { id: 'compare', label: 'Compare' },
  ]

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle>Costs · {branchLabel}</DialogTitle>
          <DialogDescription>
            Infrastructure cost for one month. Every number comes from a price entry or from an assumption. The cost of people is not part of any total.
          </DialogDescription>
        </DialogHeader>

        <div role="tablist" className="flex gap-1 border-b border-border">
          {tabs.map((t) => (
            <button
              key={t.id}
              type="button"
              role="tab"
              aria-selected={tab === t.id}
              onClick={() => setTab(t.id)}
              className={cn('-mb-px border-b-2 px-3 py-1.5 text-sm', tab === t.id ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground')}
            >
              {t.label}
            </button>
          ))}
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto pr-1">
          {tab === 'lines' && (
            <div className="space-y-4">
              {live.isError && <p role="alert" className="text-xs text-destructive">{describeError(live.error)}</p>}
              {live.data && <EstimateView estimate={live.data.estimate} hash={live.data.hash} />}
              <form className="flex flex-wrap items-end gap-2 border-t border-border pt-3" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
                <label className="block min-w-56 flex-1 text-xs font-medium text-muted-foreground">
                  Save this estimate as
                  <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder={`Estimate ${live.data?.estimate.asOf ?? ''}`} maxLength={200} />
                </label>
                <Button type="submit" size="sm" disabled={save.isPending || !live.data}>Save estimate</Button>
              </form>
              {save.isError && <p role="alert" className="text-xs text-destructive">{describeError(save.error)}</p>}
            </div>
          )}

          {tab === 'saved' && (
            <div className="space-y-2">
              {saved.data?.length === 0 && (
                <p className="text-xs leading-relaxed text-muted-foreground">
                  A saved estimate keeps its numbers. When a resource, an assumption or a price changes later, it is marked outdated and lists what changed.
                </p>
              )}
              <ul className="space-y-2">
                {saved.data?.map((s) => <SavedRow key={s.id} projectId={projectId} branchId={branchId} saved={s} onRemoved={refreshSaved} />)}
              </ul>
            </div>
          )}

          {tab === 'compare' && <CompareView projectId={projectId} branchId={branchId} branches={branches} />}
        </div>
      </DialogContent>
    </Dialog>
  )
}
