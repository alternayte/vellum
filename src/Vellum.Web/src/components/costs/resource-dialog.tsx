import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { shownInline } from '@/lib/mutation-errors'
import {
  CATEGORIES, PROVIDERS, UNKNOWN_REASON, FLAG_LABEL, costsApi, describeError, describePrice, formatAmount, priceEntryLabel, priceLabel,
  type CostAssumption, type CostResource, type EstimateLine, type PriceEntry, type ResourceInput,
} from '@/lib/costs-api'

interface Props {
  projectId: string
  branchId: string | null
  /** Null for a new resource. */
  resource: CostResource | null
  elements: { id: string; label: string }[]
  assumptions: CostAssumption[]
  prices: PriceEntry[]
  /** The lines of the live estimate for this resource. */
  lines: EstimateLine[]
  onClose: () => void
}

const labelClass = 'block text-xs font-medium text-muted-foreground'

function PriceSelect({ value, onChange, prices, label }: { value: string | null; onChange: (value: string | null) => void; prices: PriceEntry[]; label: string }) {
  const missing = value !== null && !prices.some((p) => p.id === value)
  return (
    <label className={labelClass}>
      {label}
      <select className={`${selectClass} mt-1`} value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
        <option value="">No price: the line is unknown</option>
        {missing && <option value={value}>Deleted price entry</option>}
        {prices.map((p) => <option key={p.id} value={p.id}>{describePrice(p)} · {priceEntryLabel(p)}{p.unitReadable ? '' : ' · unit not read'}</option>)}
      </select>
    </label>
  )
}

/** One line of the estimate: the amount with its conversion, or why there is none. */
function LineResult({ line }: { line: EstimateLine | undefined }) {
  if (!line) return null
  if (line.status === 'unknown') {
    return <p className="mt-1 text-[11px] text-[var(--trace-retry)]">Unknown: {UNKNOWN_REASON[line.reason!] ?? line.reason}. {line.detail}</p>
  }
  return (
    <p className="mt-1 text-[11px] text-muted-foreground">
      <span className="font-mono tabular-nums text-foreground">{formatAmount(line.amount!)} {line.currency}</span> for one month · {line.conversion}
      {' · '}quantity {line.quantity!.label} · price {priceLabel(line.price!)}
      {line.flags.map((flag) => <span key={flag} className="ml-1.5 text-[var(--trace-retry)]">{FLAG_LABEL[flag] ?? flag}</span>)}
    </p>
  )
}

/**
 * A deployment resource of one element: the baseline quantity with its price entry, and
 * the usage lines. Each usage line is one unit price times one assumption.
 */
export function ResourceDialog({ projectId, branchId, resource, elements, assumptions, prices, lines, onClose }: Props) {
  const [form, setForm] = useState<ResourceInput>(() => resource
    ? {
        elementId: resource.elementId, name: resource.name, category: resource.category, provider: resource.provider,
        region: resource.region, sku: resource.sku, quantity: resource.quantity, unit: resource.unit, priceRef: resource.priceRef,
        usage: resource.usage.map((u) => ({ ...u })),
      }
    : { elementId: elements[0]?.id ?? '', name: '', category: 'compute', provider: 'custom', region: '', sku: '', quantity: 1, unit: 'instances', priceRef: null, usage: [] })
  // The lines of the estimate describe the stored resource. They are hidden while the form differs from it.
  const [stored] = useState(() => JSON.stringify(form))
  const shown = JSON.stringify(form) === stored ? lines : []
  const set = <K extends keyof ResourceInput>(field: K, value: ResourceInput[K]) => setForm({ ...form, [field]: value })
  const setUsage = (index: number, change: Partial<ResourceInput['usage'][number]>) =>
    set('usage', form.usage.map((u, i) => (i === index ? { ...u, ...change } : u)))

  const input = (): ResourceInput => ({ ...form, unit: form.quantity === null ? null : form.unit, priceRef: form.quantity === null ? null : form.priceRef })
  const save = useMutation({
    mutationFn: () => (resource ? costsApi.changeResource(projectId, branchId, resource.id, input()) : costsApi.addResource(projectId, branchId, input())),
    onSuccess: onClose, onError: shownInline,
  })
  const remove = useMutation({ mutationFn: () => costsApi.removeResource(projectId, branchId, resource!.id), onSuccess: onClose, onError: shownInline })
  const failure = save.error ?? remove.error
  const elementMissing = resource !== null && resource.elementName === null

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{resource ? resource.name : 'New resource'}</DialogTitle>
          <DialogDescription>
            What the element runs on, for one month. Vellum computes a cost only from a price entry of the workspace. Without one, the line is unknown.
          </DialogDescription>
        </DialogHeader>

        <form id="resource-form" className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1" onSubmit={(e) => { e.preventDefault(); save.mutate() }}>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className={labelClass}>
              Element
              <select className={`${selectClass} mt-1`} value={form.elementId} onChange={(e) => set('elementId', e.target.value)} required>
                {elementMissing && <option value={resource.elementId}>Removed from the model</option>}
                {elements.map((e) => <option key={e.id} value={e.id}>{e.label}</option>)}
              </select>
            </label>
            <label className={labelClass}>
              Name
              <Input className="mt-1" value={form.name} onChange={(e) => set('name', e.target.value)} placeholder="API instances" maxLength={200} required />
            </label>
            <label className={labelClass}>
              Category
              <select className={`${selectClass} mt-1`} value={form.category} onChange={(e) => set('category', e.target.value)}>
                {CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </label>
            <label className={labelClass}>
              Provider
              <select className={`${selectClass} mt-1`} value={form.provider} onChange={(e) => set('provider', e.target.value)}>
                {PROVIDERS.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </label>
            <label className={labelClass}>
              Region
              <Input className="mt-1" value={form.region} onChange={(e) => set('region', e.target.value)} placeholder="westeurope" maxLength={100} />
            </label>
            <label className={labelClass}>
              SKU
              <Input className="mt-1" value={form.sku} onChange={(e) => set('sku', e.target.value)} placeholder="D2s v5" maxLength={200} />
            </label>
          </div>

          <fieldset className="rounded-md border border-border p-3">
            <legend className="px-1 text-xs font-medium text-foreground">Baseline</legend>
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input type="checkbox" checked={form.quantity !== null} onChange={(e) => setForm({ ...form, quantity: e.target.checked ? 1 : null, unit: form.unit ?? 'instances' })} />
              The resource has a fixed monthly quantity
            </label>
            {form.quantity !== null && (
              <div className="mt-2 grid gap-3 sm:grid-cols-[8rem_10rem_minmax(0,1fr)]">
                <label className={labelClass}>
                  Quantity
                  <Input className="mt-1" type="number" min={0} step="any" value={form.quantity} onChange={(e) => set('quantity', e.target.value === '' ? 0 : Number(e.target.value))} required />
                </label>
                <label className={labelClass}>
                  Unit
                  <Input className="mt-1" value={form.unit ?? ''} onChange={(e) => set('unit', e.target.value)} placeholder="instances, hours, GB" maxLength={50} required />
                </label>
                <PriceSelect label="Price entry" value={form.priceRef} onChange={(value) => set('priceRef', value)} prices={prices} />
              </div>
            )}
            <LineResult line={shown.find((l) => l.kind === 'baseline')} />
            <p className="mt-2 text-[11px] leading-relaxed text-muted-foreground">
              Instances against a price for one hour run for 730 hours, the length of a month in every estimate.
            </p>
          </fieldset>

          <fieldset className="rounded-md border border-border p-3">
            <legend className="px-1 text-xs font-medium text-foreground">Usage lines</legend>
            {assumptions.length === 0 && <p className="text-xs text-muted-foreground">Add an assumption first. A usage line multiplies a unit price by one assumption.</p>}
            <ul className="space-y-3">
              {form.usage.map((usage, index) => (
                <li key={usage.id ?? `new-${index}`}>
                  <div className="grid items-end gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
                    <label className={labelClass}>
                      Name
                      <Input className="mt-1" value={usage.name} onChange={(e) => setUsage(index, { name: e.target.value })} placeholder="Egress" maxLength={200} required />
                    </label>
                    <label className={labelClass}>
                      Assumption
                      <select className={`${selectClass} mt-1`} value={usage.assumptionId} onChange={(e) => setUsage(index, { assumptionId: e.target.value })} required>
                        {!assumptions.some((a) => a.id === usage.assumptionId) && <option value={usage.assumptionId}>Removed assumption</option>}
                        {assumptions.map((a) => <option key={a.id} value={a.id}>{a.name} · {a.value} {a.unit} · {a.label}</option>)}
                      </select>
                    </label>
                    <Button type="button" variant="ghost" size="sm" aria-label={`Remove the usage line ${usage.name}`} onClick={() => set('usage', form.usage.filter((_, i) => i !== index))}>
                      <Trash2 className="h-3.5 w-3.5" />
                    </Button>
                  </div>
                  <div className="mt-2">
                    <PriceSelect label="Price entry" value={usage.priceRef} onChange={(value) => setUsage(index, { priceRef: value })} prices={prices} />
                  </div>
                  <LineResult line={shown.find((l) => l.usageLineId !== null && l.usageLineId === usage.id)} />
                </li>
              ))}
            </ul>
            {assumptions.length > 0 && (
              <Button type="button" size="sm" variant="secondary" className="mt-3" disabled={form.usage.length >= 20}
                onClick={() => set('usage', [...form.usage, { id: null, name: '', assumptionId: assumptions[0].id, priceRef: null }])}>
                Add a usage line
              </Button>
            )}
          </fieldset>

          {prices.length === 0 && (
            <p className="text-xs leading-relaxed text-muted-foreground">
              The price book of this workspace is empty. A workspace owner adds prices under Settings, Prices.
            </p>
          )}
        </form>

        {failure && <p role="alert" className="text-xs text-destructive">{describeError(failure)}</p>}
        <div className="flex items-center justify-between gap-2">
          {resource ? <Button type="button" variant="ghost" size="sm" disabled={remove.isPending} onClick={() => remove.mutate()}>Delete</Button> : <span />}
          <div className="flex gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
            <Button type="submit" form="resource-form" size="sm" disabled={save.isPending || !form.elementId}>Save</Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
