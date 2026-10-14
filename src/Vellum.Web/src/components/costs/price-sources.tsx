import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { SecretControl } from '@/components/settings/secret-control'
import { cn } from '@/lib/utils'
import { shownInline } from '@/lib/mutation-errors'
import { ApiError, api, type SecretStatus } from '@/lib/settings-api'
import { costsApi, describeError, formatNumber, type AwsPrice, type GcpSku } from '@/lib/costs-api'

const labelClass = 'block text-xs font-medium text-muted-foreground'
const cell = 'px-3 py-2 align-top'
const MAX_PICK = 50

/** The secrets of the workspace. Only an owner reads them, so a success also says that the viewer is an owner. */
function useSecrets(workspaceId: string) {
  return useQuery({
    queryKey: ['workspaces', workspaceId, 'secrets'],
    queryFn: () => api<SecretStatus[]>('GET', `/api/workspaces/${workspaceId}/secrets`),
    retry: false,
  })
}

/** Rows a person ticks before one import. A row that was added stays marked. */
function usePicks() {
  const [picked, setPicked] = useState<string[]>([])
  const [added, setAdded] = useState<string[]>([])
  return {
    picked,
    added,
    toggle: (id: string) => setPicked((current) => (current.includes(id) ? current.filter((p) => p !== id) : [...current, id].slice(0, MAX_PICK))),
    done: (ids: string[]) => { setAdded((current) => [...current, ...ids]); setPicked([]) },
    reset: () => setPicked([]),
  }
}

function PickCell({ id, notImportable, picks }: { id: string; notImportable: string | null; picks: ReturnType<typeof usePicks> }) {
  if (notImportable) return <span className="text-muted-foreground">{notImportable}</span>
  if (picks.added.includes(id)) return <span className="text-muted-foreground">Added</span>
  return (
    <input type="checkbox" aria-label={`Pick ${id}`} checked={picks.picked.includes(id)} onChange={() => picks.toggle(id)}
      disabled={!picks.picked.includes(id) && picks.picked.length >= MAX_PICK} />
  )
}

/**
 * Custom prices from a CSV file. The server checks every line first and adds nothing when
 * one line is wrong; the errors name the line and the column.
 */
export function CsvImport({ workspaceId, onAdded }: { workspaceId: string; onAdded: () => void }) {
  const [file, setFile] = useState<{ name: string; text: string } | null>(null)
  const upload = useMutation({
    mutationFn: () => costsApi.importCsv(workspaceId, file!.text),
    onSuccess: () => { setFile(null); onAdded() },
    onError: shownInline,
  })
  const errors = upload.error instanceof ApiError ? upload.error.fields : []

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Import custom prices from a CSV file</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        The first line names the columns: <span className="font-mono">provider, region, sku, unit, unitPrice, currency, effectiveDate</span>, and optionally <span className="font-mono">description</span>.
        Each further line is one price, for example <span className="font-mono">hetzner,fsn1,CX22,hour,0.0066,EUR,2026-01-01</span>. Vellum checks every line first. If one line is wrong, it adds nothing. You are recorded as the person who entered each price.
      </p>
      <form className="mt-3 flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); upload.mutate() }}>
        <input
          type="file" accept=".csv,text/csv" aria-label="CSV file" className="text-xs text-muted-foreground file:mr-2 file:rounded-md file:border file:border-border file:bg-secondary file:px-2 file:py-1 file:text-xs file:text-foreground"
          onChange={(e) => {
            const chosen = e.target.files?.[0]
            upload.reset()
            if (!chosen) { setFile(null); return }
            void chosen.text().then((text) => setFile({ name: chosen.name, text }))
          }}
        />
        <Button type="submit" size="sm" variant="secondary" disabled={!file || upload.isPending}>{upload.isPending ? 'Importing…' : 'Import file'}</Button>
      </form>
      {upload.isSuccess && (
        <p role="status" className="mt-2 text-xs text-muted-foreground">
          Imported {upload.data.imported} {upload.data.imported === 1 ? 'price' : 'prices'}.
        </p>
      )}
      {upload.isError && (
        <div role="alert" className="mt-2 text-xs text-destructive">
          <p>{errors.length > 0 ? `Nothing was imported. Fix ${errors.length === 1 ? 'this error' : `these ${errors.length} errors`} and import the file again.` : describeError(upload.error)}</p>
          {errors.length > 0 && (
            <ul className="mt-1 max-h-48 list-disc space-y-0.5 overflow-y-auto pl-4">
              {errors.map((error, index) => (
                <li key={index}><span className="font-mono">{error.field}</span>: {error.message}</li>
              ))}
            </ul>
          )}
        </div>
      )}
    </>
  )
}

/**
 * Prices from the public AWS Price List Bulk API. Vellum reads the offer file of one service
 * in one region and lists its on-demand rows. You tick rows; Vellum reads the file again and
 * stores the rows you picked.
 */
export function AwsFetch({ workspaceId, onAdded }: { workspaceId: string; onAdded: () => void }) {
  const owner = useSecrets(workspaceId).isSuccess
  const [query, setQuery] = useState({ service: '', region: '', search: '' })
  const services = useQuery({ queryKey: ['aws', 'services', workspaceId], queryFn: () => costsApi.awsServices(workspaceId), enabled: owner, staleTime: Infinity, retry: false })
  const known = services.data?.services.includes(query.service) ?? false
  const regions = useQuery({
    queryKey: ['aws', 'regions', workspaceId, query.service], queryFn: () => costsApi.awsRegions(workspaceId, query.service),
    enabled: owner && known, staleTime: Infinity, retry: false,
  })
  const picks = usePicks()
  const search = useMutation({ mutationFn: () => costsApi.searchAws(workspaceId, query), onSuccess: picks.reset, onError: shownInline })
  const add = useMutation({
    mutationFn: () => costsApi.importAws(workspaceId, search.data!.service, search.data!.region, picks.picked),
    onSuccess: () => { picks.done(picks.picked); onAdded() },
    onError: shownInline,
  })
  if (!owner) return null

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Fetch prices from AWS</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        Vellum reads the public AWS Price List of one service in one region. It needs no credential. It lists the on-demand prices whose product holds every word you enter. You tick the rows. Vellum then reads the file again and stores each price with its rate code, its effective date and the address of the file. A file larger than 200 MB, such as EC2 in a large region, is not read: enter that price as a custom price.
      </p>
      <form className="mt-3 grid items-end gap-2 sm:grid-cols-4" onSubmit={(e) => { e.preventDefault(); search.mutate() }}>
        <label className={labelClass}>
          Service
          <Input className="mt-1" list={`aws-services-${workspaceId}`} placeholder={services.isPending ? 'Loading…' : 'AWSLambda'} maxLength={64} required
            value={query.service} onChange={(e) => setQuery({ ...query, service: e.target.value.trim(), region: '' })} />
          <datalist id={`aws-services-${workspaceId}`}>
            {services.data?.services.map((s) => <option key={s} value={s} />)}
          </datalist>
        </label>
        <label className={labelClass}>
          Region
          <select className={cn(selectClass, 'mt-1')} required value={query.region} disabled={!regions.data}
            onChange={(e) => setQuery({ ...query, region: e.target.value })}>
            <option value="">{regions.isFetching ? 'Loading…' : known ? 'Pick a region' : 'Pick a service first'}</option>
            {regions.data?.regions.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </label>
        <label className={cn(labelClass, 'sm:col-span-2')}>
          Words in the product
          <Input className="mt-1" placeholder="t3.micro Linux Shared" maxLength={100} value={query.search} onChange={(e) => setQuery({ ...query, search: e.target.value })} />
        </label>
        <div className="sm:col-span-4">
          <Button type="submit" size="sm" variant="secondary" disabled={search.isPending || !known || !query.region}>{search.isPending ? 'Reading the price list…' : 'Search prices'}</Button>
        </div>
      </form>
      {services.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(services.error)}</p>}
      {regions.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(regions.error)}</p>}
      {search.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(search.error)}</p>}

      {search.data && (
        <>
          <PickBar count={search.data.items.length} more={search.data.more} picked={picks.picked.length} pending={add.isPending} onAdd={() => add.mutate()} />
          {add.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(add.error)}</p>}
          {search.data.items.length > 0 && (
            <div className="mt-2 max-h-96 overflow-auto rounded-lg border border-border">
              <table className="w-full text-left text-xs">
                <thead className="sticky top-0 border-b border-border bg-card text-muted-foreground">
                  <tr>
                    <th className={cell}></th><th className={`${cell} font-medium`}>Product</th><th className={`${cell} font-medium`}>Price</th>
                    <th className={`${cell} text-right font-medium`}>Unit price</th><th className={`${cell} font-medium`}>Effective</th>
                  </tr>
                </thead>
                <tbody>
                  {search.data.items.map((row: AwsPrice) => (
                    <tr key={row.rateCode} className="border-b border-border last:border-b-0">
                      <td className={cell}><PickCell id={row.rateCode} notImportable={row.notImportable} picks={picks} /></td>
                      <td className={cell}>
                        {row.attributes.instanceType ?? row.attributes.usagetype ?? row.sku}
                        <span className="block text-muted-foreground">{row.productFamily}</span>
                      </td>
                      <td className={cell}>
                        {row.description}
                        <span className="block font-mono text-[10px] text-muted-foreground">{row.rateCode}</span>
                      </td>
                      <td className={`${cell} whitespace-nowrap text-right font-mono tabular-nums`}>
                        {formatNumber(row.pricePerUnit)} {row.currency}
                        <span className="block font-sans text-muted-foreground">per {row.unitOfMeasure}</span>
                      </td>
                      <td className={`${cell} whitespace-nowrap text-muted-foreground`}>{row.effectiveDate ?? 'not stated'}</td>
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

function PickBar({ count, more, picked, pending, onAdd }: { count: number; more: boolean; picked: number; pending: boolean; onAdd: () => void }) {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-3">
      <p className="text-xs text-muted-foreground">
        {count} {count === 1 ? 'row' : 'rows'}.{more ? ' The price list has more matching rows than Vellum shows. Add words to narrow the search.' : ''}
      </p>
      <Button size="sm" variant="secondary" disabled={picked === 0 || pending} onClick={onAdd}>
        {pending ? 'Adding…' : `Add ${picked} to the price book`}
      </Button>
    </div>
  )
}

/**
 * Prices from the Google Cloud Billing Catalog API. It needs an API key, stored encrypted as
 * the workspace secret gcp:apiKey and sent to cloudbilling.googleapis.com only.
 */
export function GcpFetch({ workspaceId, currency, onAdded }: { workspaceId: string; currency: string; onAdded: () => void }) {
  const secrets = useSecrets(workspaceId)
  const key = secrets.data?.find((s) => s.name === 'gcp:apiKey')
  const keySet = key?.isSet ?? false
  const [query, setQuery] = useState({ service: '', region: '', search: '', currency })
  const services = useQuery({
    queryKey: ['gcp', 'services', workspaceId, key?.fingerprint ?? ''], queryFn: () => costsApi.gcpServices(workspaceId),
    enabled: keySet, staleTime: Infinity, retry: false,
  })
  const serviceId = services.data?.services.find((s) => s.displayName === query.service || s.id === query.service)?.id ?? null
  const picks = usePicks()
  const search = useMutation({
    mutationFn: () => costsApi.searchGcp(workspaceId, { serviceId: serviceId!, region: query.region, search: query.search, currency: query.currency }),
    onSuccess: picks.reset,
    onError: shownInline,
  })
  const add = useMutation({
    mutationFn: () => costsApi.importGcp(workspaceId, search.data!.serviceId, query.region, query.currency, picks.picked),
    onSuccess: () => { picks.done(picks.picked); onAdded() },
    onError: shownInline,
  })
  if (!secrets.isSuccess) return null

  return (
    <>
      <h3 className="mt-8 text-sm font-medium text-foreground">Fetch prices from Google Cloud</h3>
      <p className="mt-1 max-w-prose text-xs leading-relaxed text-muted-foreground">
        Vellum reads the Cloud Billing Catalog API with an API key of a Google Cloud project that has the Cloud Billing API turned on. Vellum stores the key encrypted and sends it to cloudbilling.googleapis.com only. You tick the SKUs. Vellum then reads them again and stores each price with its SKU ID, its effective date and the address it came from.
      </p>
      <div className="mt-3">
        <SecretControl workspaceId={workspaceId} name="gcp:apiKey" label="Cloud Billing API key" status={key} canEdit />
      </div>
      {keySet && (
        <form className="mt-3 grid items-end gap-2 sm:grid-cols-4" onSubmit={(e) => { e.preventDefault(); search.mutate() }}>
          <label className={labelClass}>
            Service
            <Input className="mt-1" list={`gcp-services-${workspaceId}`} placeholder={services.isPending ? 'Loading…' : 'Compute Engine'} maxLength={200} required
              value={query.service} onChange={(e) => setQuery({ ...query, service: e.target.value })} />
            <datalist id={`gcp-services-${workspaceId}`}>
              {services.data?.services.map((s) => <option key={s.id} value={s.displayName}>{s.id}</option>)}
            </datalist>
          </label>
          <label className={labelClass}>Region<Input className="mt-1" placeholder="europe-west1" maxLength={40} value={query.region} onChange={(e) => setQuery({ ...query, region: e.target.value.trim() })} /></label>
          <label className={labelClass}>Words in the SKU<Input className="mt-1" placeholder="N2 Instance Core" maxLength={100} value={query.search} onChange={(e) => setQuery({ ...query, search: e.target.value })} /></label>
          <label className={labelClass}>Currency<Input className="mt-1 uppercase" maxLength={3} pattern="[A-Za-z]{3}" required value={query.currency} onChange={(e) => setQuery({ ...query, currency: e.target.value.toUpperCase() })} /></label>
          <div className="sm:col-span-4">
            <Button type="submit" size="sm" variant="secondary" disabled={search.isPending || !serviceId}>{search.isPending ? 'Reading the catalog…' : 'Search SKUs'}</Button>
            {query.service && services.data && !serviceId && <span className="ml-3 text-xs text-muted-foreground">Pick a service from the list.</span>}
          </div>
        </form>
      )}
      {services.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(services.error)}</p>}
      {search.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(search.error)}</p>}

      {search.data && (
        <>
          <PickBar count={search.data.items.length} more={search.data.more} picked={picks.picked.length} pending={add.isPending} onAdd={() => add.mutate()} />
          {add.isError && <p role="alert" className="mt-2 text-xs text-destructive">{describeError(add.error)}</p>}
          {search.data.items.length > 0 && (
            <div className="mt-2 max-h-96 overflow-auto rounded-lg border border-border">
              <table className="w-full text-left text-xs">
                <thead className="sticky top-0 border-b border-border bg-card text-muted-foreground">
                  <tr>
                    <th className={cell}></th><th className={`${cell} font-medium`}>SKU</th><th className={`${cell} font-medium`}>Regions</th>
                    <th className={`${cell} text-right font-medium`}>Unit price</th><th className={`${cell} font-medium`}>Effective</th>
                  </tr>
                </thead>
                <tbody>
                  {search.data.items.map((sku: GcpSku) => (
                    <tr key={sku.skuId} className="border-b border-border last:border-b-0">
                      <td className={cell}><PickCell id={sku.skuId} notImportable={sku.notImportable} picks={picks} /></td>
                      <td className={cell}>
                        {sku.description}
                        <span className="block text-muted-foreground">{[sku.resourceFamily, sku.resourceGroup, sku.usageType].filter(Boolean).join(' · ')}</span>
                        <span className="block font-mono text-[10px] text-muted-foreground">{sku.skuId}</span>
                      </td>
                      <td className={`${cell} text-muted-foreground`}>{sku.regions.join(', ') || '—'}</td>
                      <td className={`${cell} whitespace-nowrap text-right font-mono tabular-nums`}>
                        {formatNumber(sku.unitPrice)} {sku.currency}
                        <span className="block font-sans text-muted-foreground">per {sku.usageUnitDescription || sku.usageUnit}</span>
                      </td>
                      <td className={`${cell} whitespace-nowrap text-muted-foreground`}>{sku.effectiveDate ?? 'not stated'}</td>
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
