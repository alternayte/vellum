import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { selectClass } from '@/components/settings/fields'
import { shownInline } from '@/lib/mutation-errors'
import {
  UNKNOWN_REASON, costsApi, describeError, formatMoney, formatNumber,
  type CostAssumption, type CostResource, type EstimateLine, type Money,
} from '@/lib/costs-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { useElements } from '@/hooks/use-elements'
import { AssumptionDialog } from './assumption-dialog'
import { EstimateDialog } from './estimate-dialog'
import { ResourceDialog } from './resource-dialog'

interface CostsFlyoutProps {
  projectId: string
  /** The branch the canvas shows: the stream of the active draft, or undefined for main. */
  branchId: string | undefined
  references: ScenarioReference[]
}

const headingClass = 'text-xs font-medium uppercase tracking-wider text-muted-foreground'
const addClass = 'h-6 gap-1 px-1.5 text-xs'

function sum(lines: EstimateLine[]): Money[] {
  const totals = new Map<string, number>()
  for (const line of lines) if (line.amount !== null && line.currency) totals.set(line.currency, (totals.get(line.currency) ?? 0) + line.amount)
  return [...totals.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([currency, amount]) => ({ currency, amount }))
}

/**
 * The monthly infrastructure cost of one branch: its resources, the assumptions the usage
 * lines read, and the estimate. Vellum has no price of its own. A line with no price entry
 * is listed as unknown and is in no total.
 */
export function CostsFlyout({ projectId, branchId, references }: CostsFlyoutProps) {
  const queryClient = useQueryClient()
  const [branch, setBranch] = useState<string | null>(branchId ?? null)
  const [resource, setResource] = useState<CostResource | 'new' | null>(null)
  const [assumption, setAssumption] = useState<CostAssumption | 'new' | null>(null)
  const [details, setDetails] = useState(false)
  const [variantName, setVariantName] = useState<string | null>(null)

  const key = ['projects', projectId, 'costs']
  const branches = useQuery({ queryKey: [...key, 'branches'], queryFn: () => costsApi.branches(projectId) })
  const sheet = useQuery({ queryKey: [...key, branch, 'sheet'], queryFn: () => costsApi.sheet(projectId, branch) })
  const live = useQuery({ queryKey: [...key, branch, 'estimate'], queryFn: () => costsApi.estimate(projectId, branch), staleTime: 0 })
  const book = useQuery({ queryKey: [...key, 'prices'], queryFn: () => costsApi.prices(projectId) })
  // The elements of the branch whose sheet is shown, which can be another branch than the canvas shows.
  const elements = useElements(projectId, branch ?? undefined)
  const refresh = () => queryClient.invalidateQueries({ queryKey: key })

  const copy = useMutation({ mutationFn: () => costsApi.copy(projectId, branch, null), onSuccess: refresh, onError: shownInline })
  // A variant is a fork of main. Its cost sheet starts as a copy of the sheet shown now.
  const createVariant = useMutation({
    mutationFn: async () => {
      const variant = await costsApi.createVariant(projectId, variantName!.trim())
      await costsApi.copy(projectId, variant.streamId, branch)
      return variant.streamId
    },
    onSuccess: (streamId) => {
      setVariantName(null)
      setBranch(streamId)
      refresh()
    },
    onError: shownInline,
  })

  const estimate = live.data?.estimate
  const resources = sheet.data?.resources ?? []
  const assumptions = sheet.data?.assumptions ?? []
  const elementIds = [...new Set(resources.map((r) => r.elementId))]
  const linesOf = (resourceId: string) => estimate?.lines.filter((l) => l.resourceId === resourceId) ?? []
  const isEmpty = sheet.isSuccess && resources.length === 0 && assumptions.length === 0

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className={headingClass}>Costs</span>
      </div>

      <div className="flex-1 space-y-4 overflow-y-auto p-3">
        <div>
          <label className="block text-[11px] font-medium text-muted-foreground">
            Branch
            <select className={`${selectClass} mt-1`} value={branch ?? ''} onChange={(e) => setBranch(e.target.value || null)}>
              {(branches.data ?? [{ id: branch, label: branch ? 'This branch' : 'main' }]).map((b) => <option key={b.id ?? ''} value={b.id ?? ''}>{b.label}</option>)}
            </select>
          </label>
          {variantName === null ? (
            <button type="button" className="mt-1 text-[11px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline" onClick={() => setVariantName('')}>
              New variant from main
            </button>
          ) : (
            <form className="mt-2 space-y-1.5" onSubmit={(e) => { e.preventDefault(); createVariant.mutate() }}>
              <Input aria-label="Variant name" placeholder="Variant name" value={variantName} onChange={(e) => setVariantName(e.target.value)} maxLength={200} required autoFocus />
              <p className="text-[11px] leading-relaxed text-muted-foreground">The variant forks the model of main. Its cost sheet starts as a copy of the sheet shown here.</p>
              <div className="flex gap-1.5">
                <Button type="submit" size="sm" className="flex-1" disabled={createVariant.isPending || !variantName.trim()}>Create</Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => setVariantName(null)}>Cancel</Button>
              </div>
              {createVariant.isError && <p role="alert" className="text-xs text-destructive">{describeError(createVariant.error)}</p>}
            </form>
          )}
        </div>

        {(sheet.isError || live.isError) && <p role="alert" className="text-xs text-destructive">{describeError(sheet.error ?? live.error)}</p>}

        {estimate && (
          <section aria-labelledby="costs-estimate" className="rounded-md border border-border p-3">
            <h3 id="costs-estimate" className={headingClass}>Estimate for one month</h3>
            {estimate.totals.length === 0 ? (
              <p className="mt-1.5 text-sm text-foreground">No known cost</p>
            ) : (
              <ul className="mt-1.5">
                {estimate.totals.map((total) => <li key={total.currency} className="font-mono text-base tabular-nums text-foreground">{formatMoney(total)}</li>)}
              </ul>
            )}
            <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
              {estimate.totals.length > 1 ? 'One total for each currency. Vellum converts no currency. ' : ''}
              Known lines only, as of {estimate.asOf}.
            </p>
            {estimate.unknown.length > 0 && (
              <p className="mt-1 text-xs text-[var(--trace-retry)]">
                {estimate.unknown.length} {estimate.unknown.length === 1 ? 'line is' : 'lines are'} unknown and in no total.
              </p>
            )}
            <Button size="sm" variant="secondary" className="mt-2 w-full" onClick={() => setDetails(true)}>Lines, saved estimates and compare</Button>
          </section>
        )}

        {estimate && estimate.unknown.length > 0 && (
          <section aria-labelledby="costs-unknown">
            <h3 id="costs-unknown" className={headingClass}>Unknown <span className="font-mono">{estimate.unknown.length}</span></h3>
            <ul className="mt-1.5 space-y-1">
              {estimate.unknown.map((line) => (
                <li key={line.lineId} className="rounded-md border border-dashed border-border px-2.5 py-1.5" title={line.detail}>
                  <p className="truncate text-xs text-foreground">{line.resourceName}{line.name === 'Baseline' ? '' : ` · ${line.name}`}</p>
                  <p className="text-[11px] text-[var(--trace-retry)]">{UNKNOWN_REASON[line.reason] ?? line.reason}</p>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section aria-labelledby="costs-resources">
          <div className="flex items-center justify-between">
            <h3 id="costs-resources" className={headingClass}>Resources</h3>
            <Button variant="ghost" size="sm" className={addClass} onClick={() => setResource('new')}><Plus className="h-3 w-3" />Add</Button>
          </div>
          {isEmpty && (
            <div className="mt-1.5 space-y-2">
              <p className="text-xs leading-relaxed text-muted-foreground">
                A resource is what an element runs on: instances, a database, a queue, storage. Give it a quantity and a price entry from the price book of the workspace.
              </p>
              {branch !== null && (
                <>
                  <Button size="sm" variant="secondary" className="w-full" disabled={copy.isPending} onClick={() => copy.mutate()}>Copy the sheet of main</Button>
                  {copy.isError && <p role="alert" className="text-xs text-destructive">{describeError(copy.error)}</p>}
                </>
              )}
            </div>
          )}
          {elementIds.map((elementId) => {
            const group = resources.filter((r) => r.elementId === elementId)
            return (
              <div key={elementId} className="mt-2">
                <p className="truncate text-[11px] font-medium text-muted-foreground">
                  {group[0].elementName ?? <span className="text-[var(--trace-retry)]">Element removed from the model</span>}
                </p>
                <ul className="mt-1 space-y-1">
                  {group.map((r) => {
                    const lines = linesOf(r.id)
                    const known = sum(lines)
                    const unknown = lines.filter((l) => l.status === 'unknown').length
                    return (
                      <li key={r.id}>
                        <button
                          type="button"
                          onClick={() => setResource(r)}
                          className="w-full rounded-md border border-border px-2.5 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                        >
                          <p className="flex items-baseline gap-2">
                            <span className="min-w-0 flex-1 truncate text-sm text-foreground">{r.name}</span>
                            <span className="shrink-0 font-mono text-xs tabular-nums text-foreground">
                              {known.length > 0 ? known.map(formatMoney).join(' + ') : <span className="text-[var(--trace-retry)]">unknown</span>}
                            </span>
                          </p>
                          <p className="mt-0.5 flex flex-wrap gap-x-2 text-[11px] text-muted-foreground">
                            <span>{r.category}</span>
                            {r.quantity !== null && <span>{formatNumber(r.quantity)} {r.unit} · {r.factStatus}</span>}
                            {r.usage.length > 0 && <span>{r.usage.length} usage {r.usage.length === 1 ? 'line' : 'lines'}</span>}
                            {known.length > 0 && unknown > 0 && <span className="text-[var(--trace-retry)]">{unknown} unknown</span>}
                          </p>
                        </button>
                      </li>
                    )
                  })}
                </ul>
              </div>
            )
          })}
        </section>

        <section aria-labelledby="costs-assumptions">
          <div className="flex items-center justify-between">
            <h3 id="costs-assumptions" className={headingClass}>Assumptions</h3>
            <Button variant="ghost" size="sm" className={addClass} onClick={() => setAssumption('new')}><Plus className="h-3 w-3" />Add</Button>
          </div>
          {sheet.isSuccess && assumptions.length === 0 && (
            <p className="mt-1.5 text-xs leading-relaxed text-muted-foreground">
              An assumption is a named number, such as requests for each month or stored GB. A usage line multiplies one unit price by one assumption.
            </p>
          )}
          <ul className="mt-1.5 space-y-1">
            {assumptions.map((a) => (
              <li key={a.id}>
                <button
                  type="button"
                  onClick={() => setAssumption(a)}
                  className="w-full rounded-md border border-border px-2.5 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                >
                  <p className="truncate text-sm text-foreground">{a.name}</p>
                  <p className="mt-0.5 flex flex-wrap gap-x-2 text-[11px] text-muted-foreground">
                    <span className="font-mono tabular-nums text-foreground">{formatNumber(a.value)} {a.unit}</span>
                    <span>{a.label}</span>
                    <span>from {a.effectiveDate}</span>
                  </p>
                </button>
              </li>
            ))}
          </ul>
        </section>
      </div>

      {resource && sheet.data && (
        <ResourceDialog
          projectId={projectId}
          branchId={branch}
          resource={resource === 'new' ? null : resource}
          elements={(elements.data ?? []).map((e) => ({ id: e.id, label: e.name }))}
          assumptions={assumptions}
          prices={book.data?.entries ?? []}
          lines={resource === 'new' ? [] : linesOf(resource.id)}
          onClose={() => { setResource(null); refresh() }}
        />
      )}
      {assumption && (
        <AssumptionDialog
          projectId={projectId}
          branchId={branch}
          assumption={assumption === 'new' ? null : assumption}
          subjects={references.filter((r) => r.group === 'Elements' || r.group === 'Operations' || r.group === 'Channels')}
          onClose={() => { setAssumption(null); refresh() }}
        />
      )}
      {details && (
        <EstimateDialog projectId={projectId} branchId={branch} branches={branches.data ?? []} onClose={() => { setDetails(false); refresh() }} />
      )}
    </div>
  )
}
