import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { useProject } from '@/hooks/use-model'
import { branchesApi, personName, type EntityType } from '@/lib/branches-api'
import { LABEL_ACTION_TEXT, labelAction, useFactLabel } from '@/lib/fact-labels'
import { factStatusColor, factStatusLabel, type Provenance } from '@/lib/fact-status'
import { useDraftStore } from '@/stores/draft-store'

interface FactTruthProps {
  projectId: string
  entityType: EntityType
  entityId: string
  factStatus: string | null | undefined
  provenance: Provenance | null | undefined
  /** The model cannot be changed: the label shows and the action does not. */
  readOnly?: boolean
  /** One line, for a row in a list. */
  compact?: boolean
}

/**
 * The truth label of a fact, where it came from, who accepted it, and the one label change a
 * person can make: "Accept as declared" or "Mark as unknown". There is no free choice of label.
 */
export function FactTruth({ projectId, entityType, entityId, factStatus, provenance, readOnly = false, compact = false }: FactTruthProps) {
  const branchId = useDraftStore((s) => s.activeDraftStreamId)
  const change = useFactLabel(projectId, branchId)
  const { data: project } = useProject(projectId)
  const members = useQuery({
    queryKey: ['workspaces', project?.workspaceId, 'members'],
    queryFn: () => branchesApi.members(project!.workspaceId),
    enabled: !compact && !!project && !!provenance?.acceptedBy,
  })
  const status = factStatus ?? 'declared'
  const action = readOnly ? null : labelAction(status)
  const run = () => action && change.mutate({ entityType, entityId, action })

  const button = action && (
    <Button
      type="button" variant="outline" size="sm" className="h-6 shrink-0 px-2 text-[11px]"
      disabled={change.isPending} onClick={run}
    >
      {LABEL_ACTION_TEXT[action]}
    </Button>
  )
  const badge = (
    <span className="inline-flex items-center gap-1.5 text-xs text-foreground">
      <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: factStatusColor(status) }} />
      {factStatusLabel(status)}
    </span>
  )

  if (compact) {
    // In a list a declared fact stays quiet, as on the canvas: its one action shows on hover and on focus.
    if (status === 'declared') {
      return action ? (
        <span className="flex items-center gap-2">
          <button
            type="button" disabled={change.isPending} onClick={run}
            className="rounded text-[11px] text-muted-foreground underline-offset-2 opacity-0 hover:text-foreground hover:underline focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-primary group-hover:opacity-100 pointer-coarse:opacity-100"
          >
            {LABEL_ACTION_TEXT[action]}
          </button>
          {change.isError && <span role="alert" className="text-[11px] text-[var(--trace-failure)]">{change.error.message}</span>}
        </span>
      ) : null
    }
    return (
      <span className="mt-1 flex flex-wrap items-center gap-2">
        {badge}
        {button}
        {change.isError && <span role="alert" className="text-[11px] text-[var(--trace-failure)]">{change.error.message}</span>}
      </span>
    )
  }

  const details: [string, string][] = []
  if (provenance?.source) details.push(['Source', provenance.source])
  if (provenance?.revision) details.push(['Source revision', provenance.revision])
  if (provenance?.confidence != null) details.push(['Confidence', `${Math.round(provenance.confidence * 100)}%`])
  if (provenance?.acceptedBy) details.push(['Accepted by', personName(members.data?.members, provenance.acceptedBy)])
  if (provenance?.acceptedAt) details.push(['Accepted at', new Date(provenance.acceptedAt).toLocaleString()])

  return (
    <div className="space-y-1.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        {badge}
        {button}
      </div>
      {details.length > 0 && (
        <dl className="grid grid-cols-[96px_1fr] gap-x-2 gap-y-0.5 text-[11px]">
          {details.map(([term, value]) => (
            <div key={term} className="contents">
              <dt className="text-muted-foreground">{term}</dt>
              <dd className="min-w-0 break-words font-mono text-foreground">{value}</dd>
            </div>
          ))}
        </dl>
      )}
      {change.isError && <p role="alert" className="text-[11px] leading-snug text-[var(--trace-failure)]">{change.error.message}</p>}
    </div>
  )
}
