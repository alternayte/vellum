import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Pencil, Play, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { shownInline } from '@/lib/mutation-errors'
import { formatMs, formatNumber, stressApi, StressRefused, type StressIssue, type StressProfileDefaults, type StressProfileDetail, type StressRun } from '@/lib/stress-api'
import { StressProfileDialog } from './stress-profile-dialog'
import { StressResultDialog } from './stress-result-dialog'
import { LoadTestsPanel } from './load-tests-panel'

interface StressFlyoutProps {
  projectId: string
  branchId?: string | null
  scenarios: { id: string; name: string }[]
}

/**
 * Stress profiles of the project and their stored runs. A run simulates one scenario under
 * a declared load. Its numbers are estimates; the result view says what they leave out.
 */
export function StressFlyout({ projectId, branchId, scenarios }: StressFlyoutProps) {
  const queryClient = useQueryClient()
  const profilesKey = ['projects', projectId, 'stress', 'profiles']
  const profiles = useQuery({ queryKey: profilesKey, queryFn: () => stressApi.profiles(projectId) })
  const [expanded, setExpanded] = useState<string | null>(null)
  const runs = useQuery({
    queryKey: ['projects', projectId, 'stress', 'runs', expanded],
    queryFn: () => stressApi.runs(projectId, expanded!),
    enabled: expanded !== null,
    staleTime: 0,
  })

  const [editing, setEditing] = useState<{ existing: StressProfileDetail | null; defaults: StressProfileDefaults } | null>(null)
  const [shown, setShown] = useState<StressRun | null>(null)
  const [refusal, setRefusal] = useState<{ profileId: string; title: string; issues: StressIssue[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<'simulate' | 'measure'>('simulate')

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: profilesKey })
    queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'stress', 'runs'] })
  }

  const run = useMutation({
    mutationFn: (profileId: string) => stressApi.run(projectId, profileId, branchId),
    onMutate: () => { setRefusal(null); setError(null) },
    onSuccess: (stored, profileId) => {
      setExpanded(profileId)
      setShown(stored)
      refresh()
    },
    onError: (e, profileId) => {
      setExpanded(profileId)
      if (e instanceof StressRefused) setRefusal({ profileId, title: e.message, issues: e.issues })
      else setError(e.message)
    },
  })
  const remove = useMutation({
    mutationFn: (profileId: string) => stressApi.remove(projectId, profileId),
    onSuccess: refresh,
    onError: (e) => { setError(e.message); shownInline() },
  })

  const open = async (work: () => Promise<void>) => {
    setError(null)
    try {
      await work()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The request did not complete.')
    }
  }
  const scenarioName = (id: string) => scenarios.find((s) => s.id === id)?.name ?? 'a deleted scenario'
  const profileName = (id: string) => profiles.data?.find((p) => p.id === id)?.name ?? 'Stress run'

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Stress</span>
        {tab === 'simulate' && (
          <Button
            variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" disabled={scenarios.length === 0}
            title={scenarios.length === 0 ? 'Create a scenario first' : undefined}
            onClick={() => open(async () => setEditing({ existing: null, defaults: await stressApi.defaults(projectId) }))}
          >
            <Plus className="h-3 w-3" />
            New
          </Button>
        )}
      </div>
      <div role="tablist" aria-label="Stress" className="flex border-b border-border px-2">
        {([['simulate', 'Simulate'], ['measure', 'Measure']] as const).map(([id, label]) => (
          <button
            key={id} type="button" role="tab" id={`stress-tab-${id}`} aria-selected={tab === id} aria-controls={`stress-panel-${id}`}
            tabIndex={tab === id ? 0 : -1}
            className={`-mb-px border-b-2 px-2.5 py-1.5 text-xs focus-visible:outline-2 focus-visible:outline-primary ${tab === id ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground'}`}
            onClick={() => setTab(id)}
            onKeyDown={(e) => {
              if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
              const next = id === 'simulate' ? 'measure' : 'simulate'
              setTab(next)
              document.getElementById(`stress-tab-${next}`)?.focus()
            }}
          >
            {label}
          </button>
        ))}
      </div>
      {error && <p role="alert" className="border-b border-border px-3 py-2 text-xs text-[var(--trace-failure)]">{error}</p>}

      {tab === 'measure' && (
        <div role="tabpanel" id="stress-panel-measure" aria-labelledby="stress-tab-measure" className="min-h-0 flex-1 overflow-y-auto">
          <LoadTestsPanel projectId={projectId} branchId={branchId} profiles={profiles.data ?? []} scenarios={scenarios} />
        </div>
      )}
      <div role="tabpanel" id="stress-panel-simulate" aria-labelledby="stress-tab-simulate" hidden={tab !== 'simulate'} className="flex-1 overflow-y-auto p-2">
        {profiles.isError && <p role="alert" className="px-1.5 py-1 text-xs text-destructive">{profiles.error.message}</p>}
        {profiles.data?.length === 0 && (
          <p className="px-1.5 py-1 text-xs leading-relaxed text-muted-foreground">
            A stress profile puts one scenario under load: how often flows start, how many workers each element has and how long each piece of work takes.
            A run gives estimated latency, throughput and queue depth. {scenarios.length === 0 ? 'Create a scenario first.' : ''}
          </p>
        )}
        <ul className="space-y-1.5">
          {profiles.data?.map((profile) => {
            const isOpen = expanded === profile.id
            const running = run.isPending && run.variables === profile.id
            return (
              <li key={profile.id} className="rounded-md border border-border">
                <div className="px-2.5 pt-2">
                  <button
                    type="button" aria-expanded={isOpen}
                    className="w-full rounded text-left focus-visible:outline-2 focus-visible:outline-primary"
                    onClick={() => setExpanded(isOpen ? null : profile.id)}
                  >
                    <p className="truncate text-sm text-foreground">{profile.name}</p>
                    <p className="truncate text-[11px] text-muted-foreground">
                      {scenarioName(profile.scenarioId)} · v{profile.version} · {profile.runs} {profile.runs === 1 ? 'run' : 'runs'}
                    </p>
                  </button>
                </div>
                <div className="flex items-center gap-1 px-1.5 py-1.5">
                  <Button size="sm" className="h-6 gap-1 px-2 text-xs" disabled={run.isPending} onClick={() => run.mutate(profile.id)}>
                    <Play className="h-3 w-3" /> {running ? 'Running…' : 'Run'}
                  </Button>
                  <Button
                    variant="ghost" size="sm" className="h-6 gap-1 px-2 text-xs"
                    onClick={() => open(async () => {
                      const [existing, defaults] = await Promise.all([stressApi.profile(projectId, profile.id), stressApi.defaults(projectId)])
                      setEditing({ existing, defaults })
                    })}
                  >
                    <Pencil className="h-3 w-3" /> Edit
                  </Button>
                  <div className="flex-1" />
                  <Button
                    variant="ghost" size="sm" className="h-6 w-6 p-0 text-muted-foreground" aria-label={`Delete ${profile.name}`} title="Delete the profile and its runs"
                    disabled={remove.isPending}
                    onClick={() => { if (window.confirm(`Delete “${profile.name}” and its stored runs?`)) remove.mutate(profile.id) }}
                  >
                    <Trash2 className="h-3 w-3" />
                  </Button>
                </div>

                {refusal?.profileId === profile.id && (
                  <div role="alert" className="border-t border-border px-2.5 py-2 text-xs">
                    <p className="font-medium text-[var(--trace-failure)]">{refusal.title}</p>
                    <ul className="mt-1 list-disc space-y-0.5 pl-4 text-muted-foreground">
                      {refusal.issues.map((issue, i) => <li key={i}>{issue.message}</li>)}
                    </ul>
                  </div>
                )}

                {isOpen && (
                  <div className="border-t border-border px-1.5 py-1.5">
                    {runs.data?.length === 0 && <p className="px-1 text-[11px] text-muted-foreground">No stored run yet.</p>}
                    <ul className="space-y-1">
                      {runs.data?.map((stored) => (
                        <li key={stored.id}>
                          <button
                            type="button"
                            className="w-full rounded px-1.5 py-1 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                            onClick={() => open(async () => setShown(await stressApi.getRun(projectId, stored.id)))}
                          >
                            <p className="flex items-baseline gap-1.5 text-[11px] text-muted-foreground">
                              <span className="min-w-0 flex-1 truncate">{new Date(stored.at).toLocaleString()} · v{stored.profileVersion}</span>
                              {stored.outdated && <span className="shrink-0 rounded-full border border-[var(--trace-retry)] px-1.5 text-[10px] text-[var(--trace-retry)]">outdated</span>}
                            </p>
                            <p className="font-mono text-[11px] text-foreground">
                              p95 {formatMs(stored.summary.p95Ms)} · {formatNumber(stored.summary.throughputPerSecond, 1)}/s
                              {stored.summary.failed > 0 ? ` · ${formatNumber(stored.summary.failed)} failed` : ''}
                            </p>
                            <p className="truncate text-[10px] text-muted-foreground">
                              estimated · {stored.summary.bottleneck ? `bottleneck: ${stored.summary.bottleneck}` : `no bottleneck by the rule of engine ${stored.engineVersion}`}
                            </p>
                          </button>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </li>
            )
          })}
        </ul>
      </div>

      {editing && (
        <StressProfileDialog
          projectId={projectId} branchId={branchId} scenarios={scenarios} existing={editing.existing} defaults={editing.defaults}
          onClose={() => setEditing(null)}
          onSaved={(saved, andRun) => {
            setEditing(null)
            refresh()
            if (andRun) run.mutate(saved.id)
          }}
        />
      )}
      {shown && (
        <StressResultDialog
          projectId={projectId} profileName={profileName(shown.run.profileId)} run={shown}
          runs={expanded === shown.run.profileId ? (runs.data ?? []) : []}
          onClose={() => setShown(null)}
        />
      )}
    </div>
  )
}
