import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { reviewApi } from '@/lib/review-api'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { DecisionDialog } from './decision-dialog'

interface Props {
  projectId: string
  branchId?: string | null
  references: ScenarioReference[]
}

/** The decision records (ADRs) of the project. An accepted decision is the only thing that settles a blocking finding. */
export function DecisionsTab({ projectId, branchId, references }: Props) {
  const decisions = useQuery({ queryKey: ['projects', projectId, 'decisions'], queryFn: () => reviewApi.decisions(projectId) })
  // A decision ID, or a new decision with the decision it supersedes.
  const [open, setOpen] = useState<{ id: string } | { supersedes: string | null } | null>(null)
  const list = decisions.data ?? []

  return (
    <div className="space-y-2 p-3">
      <div className="flex items-center justify-between">
        <a className="text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground" href={reviewApi.decisionExportUrl(projectId)} download>
          Export all as Markdown
        </a>
        <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setOpen({ supersedes: null })}>
          <Plus className="h-3 w-3" />
          New
        </Button>
      </div>
      {decisions.isError && <p role="alert" className="text-xs text-destructive">{decisions.error.message}</p>}
      {decisions.isSuccess && list.length === 0 && (
        <p className="text-xs leading-relaxed text-muted-foreground">
          A decision records what was decided, why, and which alternatives were not chosen. An owner accepts it. An accepted decision can settle a finding of the review.
        </p>
      )}
      <ul className="space-y-1">
        {list.map((decision) => (
          <li key={decision.id}>
            <button
              type="button" onClick={() => setOpen({ id: decision.id })}
              className="w-full rounded-md border border-border px-2.5 py-2 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
            >
              <p className="flex items-baseline gap-2">
                <span className="shrink-0 font-mono text-xs text-foreground">{decision.number}</span>
                <span className="min-w-0 flex-1 truncate text-sm text-foreground">{decision.title}</span>
              </p>
              <p className="mt-1 flex flex-wrap items-center gap-x-2 text-[11px] text-muted-foreground">
                <span className="rounded-full border border-border px-1.5">{decision.status}</span>
                <span>{decision.origin}</span>
                {decision.settles.length > 0 && <span>answers {decision.settles.length} {decision.settles.length === 1 ? 'finding' : 'findings'}</span>}
              </p>
            </button>
          </li>
        ))}
      </ul>

      {open && (
        <DecisionDialog
          // A new key gives the dialog a new form when "Supersede" opens a new decision.
          key={'id' in open ? open.id : `new-${open.supersedes}`}
          projectId={projectId}
          branchId={branchId}
          decision={'id' in open ? (list.find((d) => d.id === open.id) ?? null) : null}
          supersedes={'id' in open ? null : open.supersedes}
          decisions={list}
          references={references}
          onClose={() => setOpen(null)}
          onSupersede={(decisionId) => setOpen({ supersedes: decisionId })}
        />
      )}
    </div>
  )
}
