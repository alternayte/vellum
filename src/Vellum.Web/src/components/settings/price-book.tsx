import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { SectionHeading, selectClass } from '@/components/settings/fields'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { PROVIDERS, costsApi, describeError, formatNumber, priceEntryLabel, type AzureMeter } from '@/lib/costs-api'

const labelClass = 'block text-xs font-medium text-muted-foreground'
const cell = 'px-3 py-2 align-top'

/**
 * The price book of the workspace. An entry is a price that a person entered, or a row of
 * the Azure price API that a person picked. Vellum adds no entry by itself.
 */
export function PriceBook({ workspaceId, currency }: { workspaceId: string; currency: string }) {
  const queryClient = useQueryClient()
  const key = ['workspaces', workspaceId, 'prices']
  const book = useQuery({ queryKey: key, queryFn: () => costsApi.priceBook(workspaceId) })
  // Each change of the book writes an audit entry, so the audit log is read again too.
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['workspaces', workspaceId, 'audit'] })
    return queryClient.invalidateQueries({ queryKey: key })
  }
  const remove = useMutation({ mutationFn: (id: string) => costsApi.removePrice(workspaceId, id), onSuccess: refresh, onError: shownInline })

  return (
    <>
      <SectionHeading
        title="Prices"
        description="Vellum ships no price. An estimate uses only the entries listed here. A resource with no entry shows as unknown, never as zero. Only a workspace owner changes this list."
      />

      {book.isError && <p role="alert" className="mt-3 text-xs text-destructive">{describeError(book.error)}</p>}
      {book.data?.length === 0 && <p className="mt-4 text-sm text-muted-foreground">The price book is empty.</p>}
      {book.data && book.data.length > 0 && (
        <div className="mt-4 overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs text-muted-foreground">
              <tr>
                <th className={`${cell} font-medium`}>Provider</th><th className={`${cell} font-medium`}>SKU</th><th className={`${cell} font-medium`}>Region</th>
                <th className={`${cell} text-right font-medium`}>Unit price</th><th className={`${cell} font-medium`}>Effective</th>
                <th className={`${cell} font-medium`}>Source</th><th className={cell}></th>
              </tr>
            </thead>
            <tbody>
              {book.data.map((entry) => (
                <tr key={entry.id} className="border-b border-border last:border-b-0">
                  <td className={cell}>{entry.provider}</td>
                  <td className={cell}>
                    {entry.sku}
                    {entry.description && <span className="block text-xs text-muted-foreground">{entry.description}</span>}
                  </td>
                  <td className={cell}>{entry.region || '—'}</td>
                  <td className={`${cell} whitespace-nowrap text-right font-mono text-xs tabular-nums`}>
                    {formatNumber(entry.unitPrice)} {entry.currency}
                    <span className="block font-sans text-muted-foreground">per {entry.unit}</span>
                    {!entry.unitReadable && <span className="block font-sans text-[var(--trace-retry)]">Vellum does not read this unit</span>}
                  </td>
                  <td className={`${cell} whitespace-nowrap text-xs text-muted-foreground`}>{entry.effectiveDate}</td>
                  <td className={`${cell} text-xs text-muted-foreground`}>
                    {priceEntryLabel(entry)}
                    {entry.fetched && <span className="block font-mono text-[10px]" title={entry.fetched.url}>meter {entry.fetched.meterId}</span>}
                  </td>
                  <td className={`${cell} text-right`}>
                    <Button size="sm" variant="ghost" disabled={remove.isPending} onClick={() => remove.mutate(entry.id)}>Delete</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {remove.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(remove.error)}</p>}

      <CustomPriceForm workspaceId={workspaceId} currency={currency} onAdded={refresh} />
      <AzureSearch workspaceId={workspaceId} currency={currency} onAdded={refresh} />

      <p className="mt-8 max-w-prose text-xs leading-relaxed text-muted-foreground">
        Vellum fetches prices from Azure only. Enter the prices of AWS, Google Cloud and your own infrastructure as custom entries.
      </p>
    </>
  )
}

function CustomPriceForm({ workspaceId, currency, onAdded }: { workspaceId: string; currency: string; onAdded: () => void }) {
  const empty = { provider: 'custom', region: '', sku: '', description: '', unit: '', unitPrice: '', currency, effectiveDate: new Date().toISOString().slice(0, 10) }
  const [form, setForm] = useState(empty)
  const add = useMutation({
    mutationFn: () => costsApi.addCustomPrice(workspaceId, {
      provider: form.provider, region: form.region.trim(), sku: form.sku.trim(), description: form.description.trim() || null,
      unit: form.unit.trim(), unitPrice: Number(form.unitPrice), currency: form.currency.trim().toUpperCase(), effectiveDate: form.effectiveDate,
    }),
    onSuccess: () => { setForm({ ...empty, provider: form.provider, currency: form.currency }); onAdded() },
    onError: shownInline,
  })
  const text = (field: 'region' | 'sku' | 'description' | 'unit' | 'unitPrice' | 'currency' | 'effectiveDate') => ({
    value: form[field],
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [field]: e.target.value }),
  })

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Add a custom price</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        The price that you pay for one unit. The entry records who entered it and when. To change a price, add a new entry.
      </p>
      <form className="mt-3 grid items-end gap-2 sm:grid-cols-4" onSubmit={(e) => { e.preventDefault(); add.mutate() }}>
        <label className={labelClass}>
          Provider
          <select className={cn(selectClass, 'mt-1')} value={form.provider} onChange={(e) => setForm({ ...form, provider: e.target.value })}>
            {PROVIDERS.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </label>
        <label className={labelClass}>SKU or name<Input className="mt-1" placeholder="m6g.large" maxLength={200} required {...text('sku')} /></label>
        <label className={labelClass}>Region<Input className="mt-1" placeholder="eu-west-1" maxLength={100} {...text('region')} /></label>
        <label className={labelClass}>Effective from<Input className="mt-1" type="date" required {...text('effectiveDate')} /></label>
        <label className={labelClass}>Unit price<Input className="mt-1" type="number" min={0} step="any" required {...text('unitPrice')} /></label>
        <label className={labelClass}>Currency<Input className="mt-1 uppercase" maxLength={3} pattern="[A-Za-z]{3}" required {...text('currency')} /></label>
        <label className={labelClass}>For one<Input className="mt-1" placeholder="hour, GB/month, 1000000 requests" maxLength={50} required {...text('unit')} /></label>
        <label className={labelClass}>Note<Input className="mt-1" maxLength={200} {...text('description')} /></label>
        <div className="sm:col-span-4">
          <Button type="submit" size="sm" variant="secondary" disabled={add.isPending}>Add price</Button>
        </div>
      </form>
      {add.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(add.error)}</p>}
    </>
  )
}

function AzureSearch({ workspaceId, currency, onAdded }: { workspaceId: string; currency: string; onAdded: () => void }) {
  const [query, setQuery] = useState({ serviceName: '', region: '', skuName: '', meterName: '', currency })
  const search = useMutation({ mutationFn: () => costsApi.searchAzure(workspaceId, query), onError: shownInline })
  const [added, setAdded] = useState<string[]>([])
  const rowKey = (meter: AzureMeter) => `${meter.meterId}/${meter.skuId}/${meter.armRegionName}/${meter.tierMinimumUnits}/${meter.type}`
  const add = useMutation({
    mutationFn: (meter: AzureMeter) => costsApi.importAzure(workspaceId, meter),
    onSuccess: (_, meter) => { setAdded((current) => [...current, rowKey(meter)]); onAdded() },
    onError: shownInline,
  })
  const text = (field: keyof typeof query) => ({
    value: query[field],
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => setQuery({ ...query, [field]: e.target.value }),
  })

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Fetch a price from Azure</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        Vellum searches the public Azure Retail Prices API and shows the rows. You pick the row. Vellum then reads that row again and stores its price, its meter ID and its effective date. The values are case sensitive.
      </p>
      <form className="mt-3 grid items-end gap-2 sm:grid-cols-5" onSubmit={(e) => { e.preventDefault(); search.mutate() }}>
        <label className={labelClass}>Service name<Input className="mt-1" placeholder="Virtual Machines" maxLength={100} required {...text('serviceName')} /></label>
        <label className={labelClass}>Region<Input className="mt-1" placeholder="westeurope" maxLength={100} {...text('region')} /></label>
        <label className={labelClass}>SKU name<Input className="mt-1" placeholder="Standard_D2s_v5" maxLength={100} {...text('skuName')} /></label>
        <label className={labelClass}>Meter name contains<Input className="mt-1" placeholder="D2s v5" maxLength={100} {...text('meterName')} /></label>
        <label className={labelClass}>Currency<Input className="mt-1 uppercase" maxLength={3} pattern="[A-Za-z]{3}" required value={query.currency} onChange={(e) => setQuery({ ...query, currency: e.target.value.toUpperCase() })} /></label>
        <div className="sm:col-span-5">
          <Button type="submit" size="sm" variant="secondary" disabled={search.isPending}>{search.isPending ? 'Searching…' : 'Search meters'}</Button>
        </div>
      </form>
      {search.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(search.error)}</p>}
      {add.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(add.error)}</p>}

      {search.data && (
        <>
          <p className="mt-3 text-xs text-muted-foreground">
            {search.data.items.length} {search.data.items.length === 1 ? 'row' : 'rows'}.
            {search.data.more ? ' The API has more rows than Vellum read. Narrow the search.' : ''}
          </p>
          {search.data.items.length > 0 && (
            <div className="mt-2 max-h-96 overflow-auto rounded-lg border border-border">
              <table className="w-full text-left text-xs">
                <thead className="sticky top-0 border-b border-border bg-card text-muted-foreground">
                  <tr>
                    <th className={`${cell} font-medium`}>Product and meter</th><th className={`${cell} font-medium`}>SKU</th><th className={`${cell} font-medium`}>Region</th>
                    <th className={`${cell} text-right font-medium`}>Retail price</th><th className={`${cell} font-medium`}>Effective</th><th className={cell}></th>
                  </tr>
                </thead>
                <tbody>
                  {search.data.items.map((meter) => (
                    <tr key={rowKey(meter)} className="border-b border-border last:border-b-0">
                      <td className={cell}>{meter.productName}<span className="block text-muted-foreground">{meter.meterName}</span></td>
                      <td className={cell}>{meter.skuName}</td>
                      <td className={cell}>{meter.armRegionName || '—'}</td>
                      <td className={`${cell} text-right font-mono tabular-nums`}>
                        {formatNumber(meter.retailPrice)} {meter.currencyCode}
                        <span className="block font-sans text-muted-foreground">per {meter.unitOfMeasure}</span>
                        {meter.tierMinimumUnits > 0 && <span className="block font-sans text-muted-foreground">from {formatNumber(meter.tierMinimumUnits)} units</span>}
                      </td>
                      <td className={`${cell} text-muted-foreground`}>{meter.effectiveStartDate ?? 'not stated'}</td>
                      <td className={`${cell} text-right`}>
                        {meter.notImportable
                          ? <span className="text-muted-foreground">{meter.notImportable}</span>
                          : added.includes(rowKey(meter))
                            ? <span className="text-muted-foreground">Added</span>
                            : <Button size="sm" variant="secondary" disabled={add.isPending} onClick={() => add.mutate(meter)}>Add to the price book</Button>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </>
  )
}
