import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { shownInline } from '@/lib/mutation-errors'
import { describeCounts, isAgentActor, promisesApi } from '@/lib/promises-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { PromiseDialog } from './promise-dialog'

interface PromisesFlyoutProps {
  projectId: string
  references: ScenarioReference[]
  scenarios: { id: string; name: string }[]
}

/**
 * A promise is a claim about the system that must always hold, with a requirement ID that
 * never changes. Vellum reports what evidence each promise has. It never calls one verified.
 */
export function PromisesFlyout({ projectId, references, scenarios }: PromisesFlyoutProps) {
  const queryClient = useQueryClient()
  const promises = useQuery({ queryKey: ['projects', projectId, 'promises'], queryFn: () => promisesApi.list(projectId) })
  const prefix = useQuery({ queryKey: ['projects', projectId, 'promises', 'prefix'], queryFn: () => promisesApi.prefix(projectId) })
  const [open, setOpen] = useState<string | 'new' | null>(null)
  const [newPrefix, setNewPrefix] = useState('')
  const setPrefix = useMutation({
    mutationFn: () => promisesApi.setPrefix(projectId, newPrefix.trim().toUpperCase()),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises', 'prefix'] }),
    onError: shownInline,
  })
  const hasPrefix = Boolean(prefix.data?.prefix)

  const submitPrefix = (e: FormEvent) => {
    e.preventDefault()
    setPrefix.mutate()
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Promises</span>
        {hasPrefix && (
          <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setOpen('new')}>
            <Plus className="h-3 w-3" />
            New
          </Button>
        )}
      </div>

      <div className="flex-1 overflow-y-auto p-2">
        {prefix.isSuccess && !hasPrefix && (
          <form onSubmit={submitPrefix} className="space-y-2 p-1.5">
            <p className="text-xs leading-relaxed text-muted-foreground">
              Each promise gets a requirement ID such as ORD-014. Choose the letters for this project. You cannot change them later.
            </p>
            <label className="block text-[11px] font-medium text-muted-foreground">
              Prefix (2 to 6 letters)
              <Input className="mt-1 font-mono uppercase" value={newPrefix} onChange={(e) => setNewPrefix(e.target.value)} pattern="[A-Za-z]{2,6}" maxLength={6} required />
            </label>
            <Button type="submit" size="sm" className="w-full" disabled={setPrefix.isPending}>Set the prefix</Button>
            {setPrefix.isError && <p role="alert" className="text-xs text-destructive">{setPrefix.error.message}</p>}
            <p className="text-[11px] text-muted-foreground">Only a workspace owner can set the prefix.</p>
          </form>
        )}

        {hasPrefix && promises.data?.length === 0 && (
          <p className="px-1.5 py-1 text-xs leading-relaxed text-muted-foreground">
            A promise states what must always hold, such as “an accepted order survives a broker outage”. It links to the flows and contracts it is about, and lists the checks that would count as evidence.
          </p>
        )}
        <ul className="space-y-1">
          {promises.data?.map((promise) => (
            <li key={promise.id}>
              <button
                type="button"
                onClick={() => setOpen(promise.id)}
                className="w-full rounded-md border border-border px-2.5 py-2 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
              >
                <p className="flex items-baseline gap-2">
                  <span className="shrink-0 font-mono text-xs text-foreground">{promise.requirementId}</span>
                  <span className="min-w-0 flex-1 truncate text-sm text-foreground">{promise.title}</span>
                </p>
                <p className="mt-1 flex flex-wrap items-center gap-x-2 text-[11px] text-muted-foreground">
                  <span className="rounded-full border border-border px-1.5">{promise.state}{promise.hasDraft && promise.state !== 'draft' ? ' · draft pending' : ''}</span>
                  {promise.hasDraft && isAgentActor(promise.draftLastChangedBy) && <span className="text-[var(--trace-retry)]">draft changed by an agent</span>}
                  <span>{describeCounts(promise.counts)}</span>
                  {promise.needsReview && <span className="text-[var(--trace-retry)]">a linked part is gone</span>}
                </p>
              </button>
            </li>
          ))}
        </ul>
      </div>

      {open && (
        <PromiseDialog
          projectId={projectId}
          promiseId={open === 'new' ? null : open}
          references={references}
          scenarios={scenarios}
          onClose={() => { setOpen(null); queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'promises'] }) }}
        />
      )}
    </div>
  )
}
