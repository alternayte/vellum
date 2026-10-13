import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileCode2, FileUp, Plus, ShieldAlert, ShieldCheck, Trash2, TriangleAlert } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { loadTestsApi, LoadTestRefused, type K6Script, type LoadTestTarget, type MeasuredResult } from '@/lib/load-tests-api'
import { formatMs, formatNumber, type StressProfileSummary } from '@/lib/stress-api'
import { MeasuredLabel } from './evidence-labels'
import { ImportResultsDialog } from './import-results-dialog'
import { K6ScriptDialog } from './k6-script-dialog'
import { MeasuredResultDialog } from './measured-result-dialog'
import { TargetDialog } from './target-dialog'

interface Props {
  projectId: string
  branchId?: string | null
  profiles: StressProfileSummary[]
  scenarios: { id: string; name: string }[]
}

const selectClass =
  'mt-1 h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30'
const heading = 'text-[11px] font-medium uppercase tracking-wider text-muted-foreground'

/**
 * Measurement of a deployed system with k6: the targets a person authorised, the script for
 * a stress profile and a target, and the results that were imported. Vellum does not run k6.
 */
export function LoadTestsPanel({ projectId, branchId, profiles, scenarios }: Props) {
  const queryClient = useQueryClient()
  const targetsKey = ['projects', projectId, 'load-tests', 'targets']
  const resultsKey = ['projects', projectId, 'load-tests', 'results']
  const targets = useQuery({ queryKey: targetsKey, queryFn: () => loadTestsApi.targets(projectId) })
  const results = useQuery({ queryKey: resultsKey, queryFn: () => loadTestsApi.results(projectId), staleTime: 0 })

  const [editing, setEditing] = useState<{ existing: LoadTestTarget | null } | null>(null)
  const [chosenProfile, setChosenProfile] = useState('')
  const [chosenTarget, setChosenTarget] = useState('')
  const [script, setScript] = useState<K6Script | null>(null)
  const [refusal, setRefusal] = useState<{ title: string; reasons: string[] } | null>(null)
  const [importing, setImporting] = useState(false)
  const [shown, setShown] = useState<MeasuredResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The choice falls back to the first entry, so a list with one entry needs no choice.
  const profileId = profiles.some((p) => p.id === chosenProfile) ? chosenProfile : (profiles[0]?.id ?? '')
  const targetId = targets.data?.some((t) => t.id === chosenTarget) ? chosenTarget : (targets.data?.[0]?.id ?? '')
  const target = targets.data?.find((t) => t.id === targetId)
  const profileName = (id: string) => profiles.find((p) => p.id === id)?.name ?? null
  const scenarioName = (id: string) => scenarios.find((s) => s.id === id)?.name ?? 'a deleted scenario'

  const generate = useMutation({
    mutationFn: () => loadTestsApi.generate(projectId, targetId, profileId, branchId),
    onMutate: () => { setRefusal(null); setError(null) },
    onSuccess: setScript,
    onError: (e) => {
      if (e instanceof LoadTestRefused) setRefusal({ title: e.message, reasons: e.reasons })
      else setError(e.message)
      shownInline()
    },
  })
  const removeResult = useMutation({
    mutationFn: (id: string) => loadTestsApi.removeResult(projectId, id),
    onMutate: () => setError(null),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: resultsKey }),
    onError: (e) => { setError(e.message); shownInline() },
  })

  const open = async (id: string) => {
    setError(null)
    try {
      setShown(await loadTestsApi.result(projectId, id))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The request did not complete.')
    }
  }

  return (
    <div className="space-y-4 p-2">
      <p className="px-1.5 text-xs leading-relaxed text-muted-foreground">
        A simulation gives estimates. To measure, generate a k6 script, run it against a deployed test system, and import the result file. Vellum sends no load itself.
      </p>
      {error && <p role="alert" className="rounded-md border border-[var(--trace-failure)]/60 px-2.5 py-2 text-xs text-[var(--trace-failure)]">{error}</p>}

      <section aria-labelledby="load-test-targets" className="space-y-1.5">
        <div className="flex items-center justify-between px-1.5">
          <h3 id="load-test-targets" className={heading}>Targets</h3>
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setEditing({ existing: null })}>
            <Plus className="h-3 w-3" /> Add
          </Button>
        </div>
        {targets.isError && <p role="alert" className="px-1.5 text-xs text-destructive">{targets.error.message}</p>}
        {targets.data?.length === 0 && (
          <p className="px-1.5 text-xs leading-relaxed text-muted-foreground">No target yet. A target is the base URL of a deployed system. An owner authorises it before a script is generated for it.</p>
        )}
        <ul className="space-y-1.5">
          {targets.data?.map((t) => (
            <li key={t.id}>
              <button
                type="button" onClick={() => setEditing({ existing: t })}
                className="w-full rounded-md border border-border px-2.5 py-2 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
              >
                <span className="flex items-center gap-1.5">
                  <span className="min-w-0 flex-1 truncate text-sm text-foreground">{t.name}</span>
                  <span className={`shrink-0 rounded-full border px-1.5 text-[10px] ${t.environment === 'production' ? 'border-[var(--trace-failure)] text-[var(--trace-failure)]' : 'border-border text-muted-foreground'}`}>
                    {t.environment}
                  </span>
                </span>
                <span className="block truncate font-mono text-[11px] text-muted-foreground">{t.baseUrl}</span>
                <span className="mt-0.5 flex items-center gap-1 text-[11px]">
                  {t.authorisation ? (
                    <><ShieldCheck aria-hidden className="h-3 w-3 text-[var(--trace-request)]" /><span className="text-foreground">Authorised {new Date(t.authorisation.at).toLocaleDateString()}</span></>
                  ) : (
                    <><ShieldAlert aria-hidden className="h-3 w-3 text-[var(--trace-retry)]" /><span className="text-[var(--trace-retry)]">Not authorised</span></>
                  )}
                  {t.override && (
                    <span className="ml-auto inline-flex items-center gap-1 text-[var(--trace-failure)]"><TriangleAlert aria-hidden className="h-3 w-3" /> override set</span>
                  )}
                </span>
              </button>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="load-test-script" className="space-y-1.5">
        <h3 id="load-test-script" className={`${heading} px-1.5`}>k6 script</h3>
        {profiles.length === 0 || (targets.data?.length ?? 0) === 0 ? (
          <p className="px-1.5 text-xs leading-relaxed text-muted-foreground">
            A script needs a stress profile and a target. {profiles.length === 0 ? 'Create a stress profile under Simulate: it gives the arrival rate and the duration. ' : ''}
            {(targets.data?.length ?? 0) === 0 ? 'Add a target above.' : ''}
          </p>
        ) : (
          <form className="space-y-2 rounded-md border border-border px-2.5 py-2" onSubmit={(e) => { e.preventDefault(); generate.mutate() }}>
            <label className="block text-[11px] font-medium text-muted-foreground">
              Stress profile
              <select className={selectClass} value={profileId} onChange={(e) => setChosenProfile(e.target.value)}>
                {profiles.map((p) => <option key={p.id} value={p.id}>{p.name} · {scenarioName(p.scenarioId)}</option>)}
              </select>
            </label>
            <label className="block text-[11px] font-medium text-muted-foreground">
              Target
              <select className={selectClass} value={targetId} onChange={(e) => setChosenTarget(e.target.value)}>
                {targets.data?.map((t) => <option key={t.id} value={t.id}>{t.name} · {t.environment}{t.authorisation ? '' : ' · not authorised'}</option>)}
              </select>
            </label>
            {target?.environment === 'production' && (
              <p className="flex gap-1.5 text-[11px] leading-relaxed text-foreground">
                <TriangleAlert aria-hidden className="mt-0.5 h-3 w-3 shrink-0 text-[var(--trace-failure)]" />
                <span>
                  Production-class target. {target.override?.allowNonIdempotent ? 'The override allows requests that change data.' : 'The script leaves out every POST, PUT, PATCH and DELETE request.'}
                </span>
              </p>
            )}
            <Button type="submit" size="sm" className="h-7 gap-1.5 px-2 text-xs" disabled={generate.isPending || !profileId || !targetId}>
              <FileCode2 aria-hidden className="h-3 w-3" /> {generate.isPending ? 'Generating…' : 'Generate k6 script'}
            </Button>
            {refusal && (
              <div role="alert" className="text-xs">
                <p className="font-medium text-[var(--trace-failure)]">No script was generated.</p>
                <p className="mt-0.5 leading-relaxed text-foreground">{refusal.title}</p>
                {refusal.reasons.length > 0 && (
                  <ul className="mt-1 list-disc space-y-0.5 pl-4 text-muted-foreground">{refusal.reasons.map((reason, i) => <li key={i}>{reason}</li>)}</ul>
                )}
              </div>
            )}
          </form>
        )}
      </section>

      <section aria-labelledby="load-test-results" className="space-y-1.5">
        <div className="flex items-center justify-between px-1.5">
          <h3 id="load-test-results" className={heading}>Measured results</h3>
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setImporting(true)}>
            <FileUp className="h-3 w-3" /> Import results
          </Button>
        </div>
        {results.isError && <p role="alert" className="px-1.5 text-xs text-destructive">{results.error.message}</p>}
        {results.data?.length === 0 && <p className="px-1.5 text-xs leading-relaxed text-muted-foreground">No measured result yet. Run a generated script with k6 and import the file it writes.</p>}
        <ul className="space-y-1.5">
          {results.data?.map((r) => (
            <li key={r.id} className="flex items-stretch rounded-md border border-l-2 border-border border-l-[var(--trace-request)]">
              <button
                type="button" onClick={() => open(r.id)}
                className="min-w-0 flex-1 rounded-l-md px-2.5 py-2 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
              >
                <span className="flex flex-wrap items-center gap-1.5">
                  <MeasuredLabel />
                  {r.stale && <span className="rounded-full border border-[var(--trace-retry)] px-1.5 text-[10px] text-[var(--trace-retry)]">stale</span>}
                  <span className="min-w-0 flex-1 truncate text-right text-[11px] text-muted-foreground">{new Date(r.startedAt).toLocaleString()}</span>
                </span>
                <span className="mt-1 block font-mono text-[11px] font-semibold text-foreground">
                  p95 {formatMs(r.flow.latency?.p95Ms)} · {formatNumber(r.flow.throughputPerSecond, 1)}/s{r.flow.unexpectedStatus > 0 ? ` · ${formatNumber(r.flow.unexpectedStatus)} failed` : ''}
                </span>
                <span className="block truncate text-[10px] text-muted-foreground">
                  {scenarioName(r.scenarioId)} · {r.provenance.targetName} ({r.provenance.environment})
                </span>
              </button>
              <Button
                variant="ghost" size="sm" className="h-auto w-7 rounded-l-none p-0 text-muted-foreground" aria-label={`Delete the measured result of ${new Date(r.startedAt).toLocaleString()}`}
                title="Delete the measured result" disabled={removeResult.isPending}
                onClick={() => { if (window.confirm('Delete this measured result?')) removeResult.mutate(r.id) }}
              >
                <Trash2 className="h-3 w-3" />
              </Button>
            </li>
          ))}
        </ul>
      </section>

      {editing && (
        <TargetDialog
          projectId={projectId} existing={editing.existing}
          onClose={() => setEditing(null)}
          onChanged={(changed) => {
            queryClient.invalidateQueries({ queryKey: targetsKey })
            if (changed) setChosenTarget(changed.id)
          }}
        />
      )}
      {script && <K6ScriptDialog script={script} onClose={() => setScript(null)} />}
      {importing && (
        <ImportResultsDialog
          projectId={projectId}
          onClose={() => setImporting(false)}
          onImported={(imported) => {
            setImporting(false)
            queryClient.invalidateQueries({ queryKey: resultsKey })
            setShown(imported)
          }}
        />
      )}
      {shown && (
        <MeasuredResultDialog
          projectId={projectId} result={shown} results={results.data ?? []} profileName={profileName(shown.result.profileId)}
          onClose={() => setShown(null)}
        />
      )}
    </div>
  )
}
