import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Archive } from 'lucide-react'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DEFAULT_FILTER, RESULT_COLOR, RESULT_LABEL, SEVERITY_LABEL, countFindings, describeDisposition, filterFindings, groupFindings, reviewApi,
  type Cited, type FindingView, type RuleResult,
} from '@/lib/review-api'
import { CitedPart, Toggle } from './findings-tab'

interface Props {
  projectId: string
  snapshotId: string
  /** The hash of the findings of the model as it is now, when it is known. */
  currentHash?: string
  focusTarget: (cited: Cited) => string | null
  onFocus: (elementId: string) => void
  onClose: () => void
}

/** A finding as the snapshot holds it. It has no action: a snapshot is a record. */
function StoredFinding({ view, focusTarget, onFocus }: { view: FindingView } & Pick<Props, 'focusTarget' | 'onFocus'>) {
  const { finding } = view
  const disposition = describeDisposition(view)
  return (
    <li className="rounded-md border border-border px-2.5 py-2">
      <p className="flex flex-wrap items-center gap-1.5">
        <span className="rounded-full border px-1.5 text-[11px] font-medium" style={{ color: RESULT_COLOR[finding.result], borderColor: RESULT_COLOR[finding.result] }}>
          {RESULT_LABEL[finding.result]}
        </span>
        <CitedPart cited={finding.subject} focusTarget={focusTarget} onFocus={onFocus} />
        {view.blocks && <span className="rounded-full border border-border px-1.5 text-[11px] text-muted-foreground">Blocked Build Ready then</span>}
      </p>
      <p className="mt-1.5 text-xs leading-relaxed text-foreground">{finding.reason}</p>
      {finding.question && <p className="mt-1 text-xs font-medium text-foreground">{finding.question}</p>}
      {finding.cites.length > 0 && (
        <p className="mt-1.5 flex flex-wrap items-center gap-1">
          <span className="text-[11px] text-muted-foreground">Cites</span>
          {finding.cites.map((cited) => <CitedPart key={cited.id} cited={cited} focusTarget={focusTarget} onFocus={onFocus} />)}
        </p>
      )}
      {disposition && (
        <p className="mt-1.5 text-xs text-foreground">
          {disposition}
          {view.status === 'wont-fix' && view.reason ? <span className="text-muted-foreground"> · {view.reason}</span> : null}
        </p>
      )}
      <p className="mt-1 font-mono text-[10px] text-muted-foreground" title={finding.fingerprint}>
        rule version {finding.ruleVersion} · finding {finding.fingerprint.slice(0, 12)}
      </p>
    </li>
  )
}

/**
 * One stored snapshot of the review, read-only: the findings as they were saved, with the
 * model version, the rule set version and the hash of that time. It is not the current
 * state, and the dialog says so in its frame and in its first line.
 */
export function SnapshotDialog({ projectId, snapshotId, currentHash, focusTarget, onFocus, onClose }: Props) {
  const snapshot = useQuery({
    queryKey: ['projects', projectId, 'review', 'snapshots', snapshotId],
    queryFn: () => reviewApi.snapshot(projectId, snapshotId),
    // A snapshot never changes.
    staleTime: Infinity,
  })
  const [results, setResults] = useState<RuleResult[]>(DEFAULT_FILTER.results)
  const data = snapshot.data
  const counts = data ? countFindings(data.findings) : null
  const shown = data ? filterFindings(data.findings, { ...DEFAULT_FILTER, results }) : []
  const toggle = (result: RuleResult) => setResults((list) => (list.includes(result) ? list.filter((r) => r !== result) : [...list, result]))

  return (
    <Dialog open onOpenChange={(isOpen) => { if (!isOpen) onClose() }}>
      <DialogContent className="flex max-h-[92vh] flex-col gap-3 border-dashed sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Archive className="h-4 w-4 text-muted-foreground" aria-hidden />
            Stored snapshot{data?.note ? <> · {data.note}</> : null}
          </DialogTitle>
          <DialogDescription>
            This is a stored snapshot. It is not the current state of the review. It shows the findings and their answers as they were when a person saved them, and it never changes.
          </DialogDescription>
        </DialogHeader>

        {snapshot.isLoading && <p className="text-xs text-muted-foreground">Reading the snapshot…</p>}
        {snapshot.isError && <p role="alert" className="text-xs text-destructive">{snapshot.error.message}</p>}
        {data && counts && (
          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto pr-1">
            <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 rounded-md border border-dashed border-border p-3 text-xs">
              <dt className="text-muted-foreground">Saved</dt>
              <dd className="text-foreground">{new Date(data.createdAt).toLocaleString()}</dd>
              <dt className="text-muted-foreground">Model version</dt>
              <dd className="font-mono text-foreground">{data.modelVersion}{data.revisionId ? ` · revision ${data.revisionId}` : ''}</dd>
              <dt className="text-muted-foreground">Branch</dt>
              <dd className="truncate font-mono text-foreground" title={data.branch}>{data.branch}</dd>
              <dt className="text-muted-foreground">Rule set</dt>
              <dd className="font-mono text-foreground">{data.ruleSetVersion}</dd>
              <dt className="text-muted-foreground">Findings hash</dt>
              <dd className="break-all font-mono text-foreground">{data.hash}</dd>
              <dt className="text-muted-foreground">Inputs</dt>
              <dd className="break-all font-mono text-foreground">{data.inputsFingerprint}</dd>
              <dt className="text-muted-foreground">Against now</dt>
              <dd className="text-foreground">
                {currentHash === undefined
                  ? 'The current findings are not loaded.'
                  : currentHash === data.hash
                    ? 'The current model gives the same findings.'
                    : 'The current model gives other findings. Read the Findings tab for the current state.'}
              </dd>
            </dl>

            <p className="text-xs text-muted-foreground">
              {counts.blocking} blocked Build Ready · {counts.concerns} concerns · {counts.unknown} unknown · {counts.verified} verified · {counts.notApplicable} not applicable
              {counts.settled > 0 ? ` · ${counts.settled} settled` : ''}
            </p>
            <p className="flex flex-wrap gap-1" aria-label="Results to show">
              {(Object.keys(RESULT_LABEL) as RuleResult[]).map((r) => <Toggle key={r} value={r} label={RESULT_LABEL[r]} active={results.includes(r)} onToggle={toggle} />)}
            </p>

            {shown.length === 0 && <p className="text-xs text-muted-foreground">No stored finding has these results. {data.findings.length} are stored.</p>}
            {groupFindings(shown).map((group) => (
              <section key={group.severity} aria-label={SEVERITY_LABEL[group.severity]}>
                <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
                  {SEVERITY_LABEL[group.severity]} <span className="font-mono">{group.rules.reduce((n, r) => n + r.findings.length, 0)}</span>
                </h3>
                {group.rules.map((rule) => (
                  <div key={rule.ruleId} className="mt-2">
                    <p className="font-mono text-xs text-foreground">{rule.ruleId}</p>
                    <ul className="mt-1 space-y-1.5">
                      {rule.findings.map((view) => (
                        <StoredFinding key={view.finding.ruleId + view.finding.subject.id} view={view} focusTarget={focusTarget} onFocus={onFocus} />
                      ))}
                    </ul>
                  </div>
                ))}
              </section>
            ))}
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
