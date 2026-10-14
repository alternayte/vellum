import { ApiError, api } from '@/lib/settings-api'

export const CATEGORIES = ['compute', 'database', 'queue', 'storage', 'egress', 'other'] as const
/** The providers Vellum fetches prices from. A provider is a free name; these are suggestions. */
export const PROVIDER_SUGGESTIONS = ['azure', 'aws', 'gcp'] as const
export const ASSUMPTION_KINDS = ['requests', 'storage', 'egress', 'messages', 'other'] as const
export const ASSUMPTION_LABELS = ['declared', 'measured', 'inferred'] as const

export interface UsageLine {
  id: string
  name: string
  assumptionId: string
  priceRef: string | null
}

export interface CostResource {
  id: string
  elementId: string
  /** Null when the element is no longer in the model of the branch. */
  elementName: string | null
  name: string
  category: string
  provider: string
  region: string
  sku: string
  quantity: number | null
  unit: string | null
  priceRef: string | null
  usage: UsageLine[]
  factStatus: string
}

export interface ResourceInput {
  elementId: string
  name: string
  category: string
  provider: string
  region: string
  sku: string
  quantity: number | null
  unit: string | null
  priceRef: string | null
  usage: { id: string | null; name: string; assumptionId: string; priceRef: string | null }[]
}

export interface CostAssumption {
  id: string
  name: string
  kind: string
  subjectId: string | null
  value: number
  unit: string
  label: string
  source: string | null
  effectiveDate: string
}

export type AssumptionInput = Omit<CostAssumption, 'id'>

export interface CostSheet {
  branchId: string
  version: number
  resources: CostResource[]
  assumptions: CostAssumption[]
}

export interface Money {
  currency: string
  amount: number
}

export interface PriceSource {
  priceId: string
  /** What the price is for. Null only in an estimate that engine cost-1 saved. */
  provider: string | null
  region: string | null
  sku: string | null
  /** The provider's own ID of the meter. Null for a custom price. */
  meterId: string | null
  unitPrice: number
  unit: string
  currency: string
  source: 'custom' | 'fetched'
  effectiveDate: string
  observedOn: string
}

export interface EstimateLine {
  id: string
  resourceId: string
  usageLineId: string | null
  elementId: string
  resourceName: string
  name: string
  kind: 'baseline' | 'usage'
  category: string
  provider: string
  region: string
  status: 'known' | 'unknown'
  reason: string | null
  detail: string | null
  flags: string[]
  quantity: { value: number; unit: string; label: string; assumptionId: string | null } | null
  price: PriceSource | null
  conversion: string | null
  units: number | null
  amount: number | null
  currency: string | null
}

export interface UnknownLine {
  lineId: string
  resourceId: string
  elementId: string
  resourceName: string
  name: string
  category: string
  reason: string
  detail: string
}

export interface Estimate {
  engine: string
  asOf: string
  currency: string
  staleAfterDays: number
  lines: EstimateLine[]
  categories: { category: string; known: Money[]; knownLines: number; unknownLines: number }[]
  totals: Money[]
  unknown: UnknownLine[]
}

export interface LiveEstimate {
  branchId: string
  modelVersion: number
  sheetVersion: number
  inputsFingerprint: string
  hash: string
  estimate: Estimate
}

export interface SavedEstimate {
  id: string
  name: string
  branchId: string
  modelVersion: number
  asOf: string
  inputsFingerprint: string
  hash: string
  savedBy: string
  savedAt: string
  outdated: boolean
  changes: { kind: string; id: string | null; name: string; change: 'added' | 'removed' | 'changed' }[]
  result: Estimate | null
}

export interface AmountDelta {
  currency: string
  left: number | null
  right: number | null
  delta: number | null
}

export interface Comparison {
  comparable: boolean
  reason: string | null
  cheaper: 'left' | 'right' | 'equal' | null
  difference: Money | null
  totals: AmountDelta[]
  categories: { category: string; amounts: AmountDelta[]; leftUnknownLines: number; rightUnknownLines: number }[]
  resources: {
    elementId: string
    name: string
    change: 'added' | 'removed' | 'changed' | 'same'
    amounts: AmountDelta[]
    leftUnknownLines: number
    rightUnknownLines: number
  }[]
  leftUnknown: UnknownLine[]
  rightUnknown: UnknownLine[]
}

export interface ComparisonResult {
  left: LiveEstimate
  right: LiveEstimate
  comparison: Comparison
}

export interface PriceEntry {
  id: string
  provider: string
  region: string
  sku: string
  description: string | null
  unit: string
  unitPrice: number
  currency: string
  effectiveDate: string
  source: 'custom' | 'fetched'
  acceptedBy: string
  acceptedAt: string
  fetched: { url: string; fetchedAt: string; meterId: string; providerEffectiveDate: string | null } | null
  unitReadable: boolean
  /** The projects whose resources or saved estimates use the entry. A used entry cannot be deleted. */
  usedBy: { projectId: string; name: string }[]
}

export interface CustomPriceInput {
  provider: string
  region: string
  sku: string
  description: string | null
  unit: string
  unitPrice: number
  currency: string
  effectiveDate: string
}

export interface AzureMeter {
  meterId: string
  meterName: string
  productId: string
  productName: string
  skuId: string
  skuName: string
  armSkuName: string
  serviceName: string
  serviceFamily: string
  armRegionName: string
  location: string
  unitOfMeasure: string
  retailPrice: number
  currencyCode: string
  effectiveStartDate: string | null
  type: string
  tierMinimumUnits: number
  reservationTerm: string | null
  notImportable: string | null
}

export interface AzureMeterPage {
  items: AzureMeter[]
  more: boolean
  sourceUrl: string
}

export interface AwsPrice {
  sku: string
  rateCode: string
  productFamily: string
  attributes: Record<string, string>
  description: string
  unit: string
  unitOfMeasure: string
  pricePerUnit: number
  currency: string
  beginRange: string
  endRange: string
  effectiveDate: string | null
  notImportable: string | null
}

export interface AwsPricePage {
  service: string
  region: string
  items: AwsPrice[]
  more: boolean
  sourceUrl: string
  publicationDate: string | null
}

export interface GcpSku {
  skuId: string
  description: string
  serviceDisplayName: string
  resourceFamily: string
  resourceGroup: string
  usageType: string
  regions: string[]
  unit: string
  usageUnit: string
  usageUnitDescription: string
  unitPrice: number
  currency: string
  tiers: number
  effectiveDate: string | null
  notImportable: string | null
}

export interface GcpSkuPage {
  serviceId: string
  items: GcpSku[]
  more: boolean
  sourceUrl: string
}

export interface Branch {
  /** The stream ID of the branch, or null for main. */
  id: string | null
  label: string
}

const costs = (projectId: string) => `/api/projects/${projectId}/costs`
const prices = (workspaceId: string) => `/api/workspaces/${workspaceId}/prices`
const on = (branchId: string | null | undefined, first = true) => (branchId ? `${first ? '?' : '&'}branchId=${branchId}` : '')

export const costsApi = {
  sheet: (projectId: string, branchId: string | null) => api<CostSheet>('GET', `${costs(projectId)}${on(branchId)}`),
  addResource: (projectId: string, branchId: string | null, input: ResourceInput) =>
    api<CostSheet>('POST', `${costs(projectId)}/resources${on(branchId)}`, input),
  changeResource: (projectId: string, branchId: string | null, id: string, input: ResourceInput) =>
    api<CostSheet>('PUT', `${costs(projectId)}/resources/${id}${on(branchId)}`, input),
  removeResource: (projectId: string, branchId: string | null, id: string) =>
    api<CostSheet>('DELETE', `${costs(projectId)}/resources/${id}${on(branchId)}`),
  addAssumption: (projectId: string, branchId: string | null, input: AssumptionInput) =>
    api<CostSheet>('POST', `${costs(projectId)}/assumptions${on(branchId)}`, input),
  changeAssumption: (projectId: string, branchId: string | null, id: string, input: AssumptionInput) =>
    api<CostSheet>('PUT', `${costs(projectId)}/assumptions/${id}${on(branchId)}`, input),
  removeAssumption: (projectId: string, branchId: string | null, id: string) =>
    api<CostSheet>('DELETE', `${costs(projectId)}/assumptions/${id}${on(branchId)}`),
  copy: (projectId: string, branchId: string | null, from: string | null) =>
    api<CostSheet>('POST', `${costs(projectId)}/copy${on(branchId)}`, { from: from ?? 'main' }),

  estimate: (projectId: string, branchId: string | null) => api<LiveEstimate>('GET', `${costs(projectId)}/estimate${on(branchId)}`),
  saved: (projectId: string, branchId: string | null) => api<SavedEstimate[]>('GET', `${costs(projectId)}/estimates${on(branchId)}`),
  savedOne: (projectId: string, branchId: string | null, id: string) =>
    api<SavedEstimate>('GET', `${costs(projectId)}/estimates/${id}${on(branchId)}`),
  save: (projectId: string, branchId: string | null, name: string) =>
    api<SavedEstimate>('POST', `${costs(projectId)}/estimates${on(branchId)}`, { name: name || null, asOf: null }),
  removeSaved: (projectId: string, branchId: string | null, id: string) =>
    api<void>('DELETE', `${costs(projectId)}/estimates/${id}${on(branchId)}`),
  compare: (projectId: string, left: string | null, right: string | null) =>
    api<ComparisonResult>('GET', `${costs(projectId)}/compare?left=${left ?? 'main'}&right=${right ?? 'main'}`),

  /** The price book of the project's workspace, sorted by provider and SKU. */
  prices: (projectId: string) => api<{ workspaceId: string; entries: PriceEntry[] }>('GET', `${costs(projectId)}/prices`),

  /** Every open branch of the project: main, the open drafts and the open variants. */
  branches: async (projectId: string): Promise<Branch[]> => {
    const [drafts, variants] = await Promise.all([
      api<{ items: { name: string; streamId: string }[] }>('GET', `/api/projects/${projectId}/drafts?status=open&limit=200`),
      api<{ name: string; streamId: string; status: string }[]>('GET', `/api/projects/${projectId}/variants`),
    ])
    return [
      { id: null, label: 'main' },
      ...drafts.items.map((d) => ({ id: d.streamId, label: `Draft: ${d.name}` })),
      ...variants.filter((v) => v.status === 'open').map((v) => ({ id: v.streamId, label: `Variant: ${v.name}` })),
    ]
  },
  /** A variant forks the model of main. The server copies the cost sheet of `costSheetFrom` into it in the same transaction. */
  createVariant: (projectId: string, name: string, costSheetFrom: string | null) =>
    api<{ streamId: string }>('POST', `/api/projects/${projectId}/variants`, {
      id: crypto.randomUUID(), name, baseRevisionId: null, costSheetFrom: costSheetFrom ?? 'main',
    }),

  /** The whole price book of the workspace. The server bounds it at 2000 entries. */
  priceBook: async (workspaceId: string): Promise<PriceEntry[]> => {
    const entries: PriceEntry[] = []
    let cursor: string | null = null
    do {
      const page: { items: PriceEntry[]; cursor: string | null } = await api(
        'GET', `${prices(workspaceId)}?limit=200${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`)
      entries.push(...page.items)
      cursor = page.cursor
    } while (cursor)
    return entries.sort((a, b) => `${a.provider} ${a.sku} ${a.region}`.localeCompare(`${b.provider} ${b.sku} ${b.region}`))
  },
  addCustomPrice: (workspaceId: string, input: CustomPriceInput) => api<PriceEntry>('POST', prices(workspaceId), input),
  removePrice: (workspaceId: string, id: string) => api<void>('DELETE', `${prices(workspaceId)}/${id}`),
  searchAzure: (workspaceId: string, query: { serviceName: string; region: string; skuName: string; meterName: string; currency: string }) => {
    const params = new URLSearchParams()
    for (const [name, value] of Object.entries(query)) if (value.trim()) params.set(name, value.trim())
    return api<AzureMeterPage>('GET', `${prices(workspaceId)}/azure/meters?${params}`)
  },
  importAzure: (workspaceId: string, meter: AzureMeter) =>
    api<PriceEntry>('POST', `${prices(workspaceId)}/azure`, {
      meterId: meter.meterId, skuId: meter.skuId || null, armRegionName: meter.armRegionName || null, currency: meter.currencyCode,
    }),
  /** Every line of the file is checked first. A file with one bad line adds nothing. */
  importCsv: (workspaceId: string, csv: string) =>
    api<{ imported: number; entries: PriceEntry[] }>('POST', `${prices(workspaceId)}/csv`, { csv }),
  awsServices: (workspaceId: string) => api<{ services: string[] }>('GET', `${prices(workspaceId)}/aws/services`),
  awsRegions: (workspaceId: string, service: string) =>
    api<{ service: string; regions: string[] }>('GET', `${prices(workspaceId)}/aws/regions?service=${encodeURIComponent(service)}`),
  searchAws: (workspaceId: string, query: { service: string; region: string; search: string }) =>
    api<AwsPricePage>('GET', `${prices(workspaceId)}/aws/prices?${new URLSearchParams({ service: query.service, region: query.region, search: query.search.trim() })}`),
  importAws: (workspaceId: string, service: string, region: string, rateCodes: string[]) =>
    api<PriceEntry[]>('POST', `${prices(workspaceId)}/aws`, { service, region, rateCodes }),
  gcpServices: (workspaceId: string) =>
    api<{ services: { id: string; displayName: string }[]; more: boolean }>('GET', `${prices(workspaceId)}/gcp/services`),
  searchGcp: (workspaceId: string, query: { serviceId: string; region: string; search: string; currency: string }) => {
    const params = new URLSearchParams({ serviceId: query.serviceId, currency: query.currency })
    if (query.region.trim()) params.set('region', query.region.trim())
    if (query.search.trim()) params.set('search', query.search.trim())
    return api<GcpSkuPage>('GET', `${prices(workspaceId)}/gcp/skus?${params}`)
  },
  importGcp: (workspaceId: string, serviceId: string, region: string, currency: string, skuIds: string[]) =>
    api<PriceEntry[]>('POST', `${prices(workspaceId)}/gcp`, { serviceId, region: region.trim() || null, currency, skuIds }),
}

/** The message of a failed call, with the first refused field when the server names one. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError && error.fields.length > 0) return `${error.fields[0].field}: ${error.fields[0].message}`
  return error instanceof Error ? error.message : 'The request did not complete.'
}

/** An amount with two to four decimal places. The server rounds a line to four. */
export function formatAmount(amount: number): string {
  return amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 })
}

export function formatMoney(money: Money): string {
  return `${formatAmount(money.amount)} ${money.currency}`
}

/** A number as entered, with no rounding. */
export function formatNumber(value: number): string {
  return value.toLocaleString('en-US', { maximumFractionDigits: 10 })
}

/** Where a price comes from, as the label next to a number: "custom 2026-01-01" or "fetched 2026-10-13". */
export function priceLabel(price: { source: string; observedOn: string }): string {
  return `${price.source} ${price.observedOn}`
}

/** What a price is for, next to the number that used it: "azure · westeurope · D2s v5". */
export function priceFor(price: { provider: string | null; region: string | null; sku: string | null }): string {
  return [price.provider, price.region, price.sku].filter((part) => part).join(' · ')
}

export function priceEntryLabel(entry: PriceEntry): string {
  return entry.source === 'fetched' ? `fetched ${entry.fetched!.fetchedAt.slice(0, 10)}` : `custom ${entry.acceptedAt.slice(0, 10)}`
}

export function describePrice(entry: PriceEntry): string {
  return `${entry.provider} · ${entry.sku}${entry.region ? ` · ${entry.region}` : ''} · ${formatNumber(entry.unitPrice)} ${entry.currency} per ${entry.unit}`
}

export const UNKNOWN_REASON: Record<string, string> = {
  'no-price': 'No price',
  'price-not-found': 'Price entry deleted',
  'price-not-effective': 'Price not yet effective',
  'unit-mismatch': 'Units do not convert',
  'missing-assumption': 'Assumption missing',
  'assumption-not-effective': 'Assumption not yet effective',
  'element-not-in-model': 'Element removed from the model',
  'price-provider-mismatch': 'Price is for another provider',
  'price-region-mismatch': 'Price is for another region',
  'amount-out-of-range': 'Amount too large',
}

export const NOT_COMPARABLE: Record<string, string> = {
  'left-has-unknown-lines': 'The left side has lines with no known cost.',
  'right-has-unknown-lines': 'The right side has lines with no known cost.',
  'both-sides-have-unknown-lines': 'Both sides have lines with no known cost.',
  'several-currencies': 'The amounts are in more than one currency. Vellum converts no currency.',
  'left-has-no-lines': 'The left side has an empty cost sheet.',
  'right-has-no-lines': 'The right side has an empty cost sheet.',
  'both-sides-have-no-lines': 'Both sides have an empty cost sheet.',
}

/** The reasons that say a side has no cost sheet, as opposed to a line with no known cost. */
export function isEmptySide(reason: string | null): boolean {
  return reason === 'left-has-no-lines' || reason === 'right-has-no-lines' || reason === 'both-sides-have-no-lines'
}

export const FLAG_LABEL: Record<string, string> = {
  'stale-price': 'stale price',
  'currency-mismatch': 'other currency',
}
