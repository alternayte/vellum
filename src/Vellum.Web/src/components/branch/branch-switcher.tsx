import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Check, ChevronDown, GitBranch, Lock } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { branchesApi, personName, type Role } from '@/lib/branches-api'
import { cn } from '@/lib/utils'
import { useDraftStore, type ModelSelection } from '@/stores/draft-store'
import { AbandonDraftDialog, CreateBranchDialog, type BranchKind } from './create-branch-dialog'
import { CompareDialog } from './compare-dialog'
import { MergeDialog } from './merge-dialog'

interface Props {
  projectId: string
  workspaceId: string
  role: Role
}

type Open = { dialog: 'create'; kind: BranchKind } | { dialog: 'merge' } | { dialog: 'abandon' } | { dialog: 'compare' } | null

const KIND_LABEL: Record<ModelSelection['kind'], string> = { main: 'main', draft: 'Draft', variant: 'Variant', revision: 'Revision' }

const headingClass = 'px-2 pt-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground'
const actionClass = 'w-full rounded-md px-2 py-1.5 text-left text-sm text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary'

const sameSelection = (a: ModelSelection, b: ModelSelection) =>
  a.kind === b.kind && (a.kind === 'main' || a.id === (b as { id: string }).id)

/**
 * Says which model the workbench shows and changes it: main, an open draft, a variant, or a
 * named revision, which is read-only. It also starts a draft, a revision and a variant,
 * opens the diff of a draft against main, and opens the comparison of two models.
 */
export function BranchSwitcher({ projectId, workspaceId, role }: Props) {
  const selection = useDraftStore((s) => s.selection)
  const select = useDraftStore((s) => s.select)
  const [popover, setPopover] = useState(false)
  const [open, setOpen] = useState<Open>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const canEdit = role !== 'Viewer'

  const key = ['projects', projectId]
  const drafts = useQuery({ queryKey: [...key, 'drafts', 'open'], queryFn: () => branchesApi.drafts(projectId) })
  const variants = useQuery({ queryKey: [...key, 'variants'], queryFn: () => branchesApi.variants(projectId) })
  const revisions = useQuery({ queryKey: [...key, 'revisions'], queryFn: () => branchesApi.revisions(projectId) })
  const members = useQuery({ queryKey: ['workspaces', workspaceId, 'members'], queryFn: () => branchesApi.members(workspaceId) })
  const who = (userId: string) => personName(members.data?.members, userId)
  const when = (at: string) => new Date(at).toLocaleDateString()

  // A draft that another session merged or abandoned is no model any more. The workbench goes back to main.
  const draftGone = selection.kind === 'draft' && drafts.isSuccess && !drafts.isFetching && !drafts.data.some((d) => d.id === selection.id)
  useEffect(() => {
    if (draftGone) select({ kind: 'main' })
  }, [draftGone, select])

  const choose = (next: ModelSelection) => {
    select(next)
    setPopover(false)
  }
  // A dialog gives the focus back to the control that had it when the dialog opened. The
  // item of the list is gone by then, so the focus goes to the button of the list first.
  const start = (next: Open) => {
    setPopover(false)
    trigger.current?.focus()
    setOpen(next)
  }

  const item = (next: ModelSelection, title: string, detail: string | null, note?: string | null) => {
    const current = sameSelection(selection, next)
    return (
      <li key={next.kind === 'main' ? 'main' : next.id}>
        <button
          type="button"
          aria-current={current ? 'true' : undefined}
          onClick={() => choose(next)}
          className={cn('flex w-full items-start gap-2 rounded-md px-2 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary', current && 'bg-muted')}
        >
          <Check className={cn('mt-0.5 h-3.5 w-3.5 shrink-0 text-primary', !current && 'invisible')} aria-hidden="true" />
          <span className="min-w-0 flex-1">
            <span className="block truncate text-sm text-foreground">{title}</span>
            {detail && <span className="block truncate text-[11px] text-muted-foreground">{detail}</span>}
            {note && <span className="block truncate text-[11px] text-muted-foreground">{note}</span>}
          </span>
        </button>
      </li>
    )
  }

  const revisionName = (id: string | null) => revisions.data?.find((r) => r.id === id)?.name

  return (
    <>
      <Popover open={popover} onOpenChange={setPopover}>
        <PopoverTrigger asChild>
          <button
            ref={trigger}
            type="button"
            aria-label={`Model shown: ${selection.kind === 'main' ? 'main' : `${KIND_LABEL[selection.kind]} ${selection.name}`}${selection.kind === 'revision' ? ', read-only' : ''}. Change the model`}
            className={cn(
              'flex h-8 min-w-0 max-w-[8.5rem] shrink-0 items-center gap-1.5 rounded-lg border border-border px-2 text-xs text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary sm:max-w-xs',
              selection.kind !== 'main' && 'border-primary/60',
              selection.kind === 'revision' && 'border-dashed',
            )}
          >
            {selection.kind === 'revision'
              ? <Lock className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              : <GitBranch className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />}
            {selection.kind !== 'main' && <span className="shrink-0 text-muted-foreground max-sm:hidden">{KIND_LABEL[selection.kind]}</span>}
            <span className="min-w-0 truncate font-medium">{selection.kind === 'main' ? 'main' : selection.name}</span>
            {selection.kind === 'revision' && <span className="shrink-0 rounded-full border border-border px-1.5 text-[10px] text-muted-foreground max-sm:hidden">read-only</span>}
            <ChevronDown className="h-3 w-3 shrink-0 text-muted-foreground" aria-hidden="true" />
          </button>
        </PopoverTrigger>
        {selection.kind === 'draft' && (
          <Button type="button" size="sm" variant="secondary" className="shrink-0 max-sm:hidden" onClick={() => setOpen({ dialog: 'merge' })}>
            {canEdit ? 'Diff and merge' : 'Diff against main'}
          </Button>
        )}
        <PopoverContent align="start" className="max-h-[min(34rem,calc(100vh-5rem))] w-80 max-w-[calc(100vw-2rem)] gap-0 overflow-y-auto p-1.5">
          <p className={headingClass}>Model shown</p>
          <ul className="mt-1">{item({ kind: 'main' }, 'main', 'The model of the project')}</ul>

          <p className={headingClass}>Drafts</p>
          <ul className="mt-1">
            {(drafts.data ?? []).map((d) => item({ kind: 'draft', id: d.id, streamId: d.streamId, name: d.name }, d.name, `From main at version ${d.forkVersion} · ${who(d.createdBy)} · ${when(d.createdAt)}`))}
          </ul>
          {drafts.data?.length === 0 && <p className="px-2 py-1 text-xs text-muted-foreground">No open draft.</p>}

          <p className={headingClass}>Variants</p>
          <ul className="mt-1">
            {(variants.data ?? []).map((v) => item(
              { kind: 'variant', id: v.id, streamId: v.streamId, name: v.name }, v.name,
              `From ${v.baseRevisionId ? `revision ${revisionName(v.baseRevisionId) ?? v.baseRevisionId.slice(0, 8)}` : `main at version ${v.forkVersion}`} · ${who(v.createdBy)} · ${when(v.createdAt)}`,
            ))}
          </ul>
          {variants.data?.length === 0 && <p className="px-2 py-1 text-xs text-muted-foreground">No variant.</p>}

          <p className={headingClass}>Revisions · read-only</p>
          <ul className="mt-1">
            {(revisions.data ?? []).map((r) => item({ kind: 'revision', id: r.id, name: r.name }, r.name, `Main at version ${r.version} · ${who(r.createdBy)} · ${when(r.createdAt)}`, r.note))}
          </ul>
          {revisions.data?.length === 0 && <p className="px-2 py-1 text-xs text-muted-foreground">No named revision.</p>}
          {(drafts.isError || variants.isError || revisions.isError) && (
            <p role="alert" className="px-2 py-1 text-xs text-destructive">{(drafts.error ?? variants.error ?? revisions.error)?.message}</p>
          )}

          <div className="mt-2 border-t border-border pt-1.5">
            {selection.kind === 'draft' && (
              <>
                <button type="button" className={actionClass} onClick={() => start({ dialog: 'merge' })}>Diff against main{canEdit ? ' and merge' : ''}…</button>
                {canEdit && <button type="button" className={cn(actionClass, 'text-destructive')} onClick={() => start({ dialog: 'abandon' })}>Abandon this draft…</button>}
              </>
            )}
            {canEdit && (
              <>
                <button type="button" className={actionClass} onClick={() => start({ dialog: 'create', kind: 'draft' })}>New draft from main…</button>
                <button type="button" className={actionClass} onClick={() => start({ dialog: 'create', kind: 'revision' })}>Name a revision of main…</button>
                <button type="button" className={actionClass} onClick={() => start({ dialog: 'create', kind: 'variant' })}>New variant…</button>
              </>
            )}
            <button type="button" className={actionClass} onClick={() => start({ dialog: 'compare' })}>Compare two models…</button>
          </div>
        </PopoverContent>
      </Popover>

      {open?.dialog === 'create' && (
        <CreateBranchDialog projectId={projectId} kind={open.kind} revisions={revisions.data ?? []} selection={selection} onClose={() => setOpen(null)} />
      )}
      {open?.dialog === 'merge' && selection.kind === 'draft' && (
        <MergeDialog projectId={projectId} draft={selection} canMerge={canEdit} onClose={() => setOpen(null)} />
      )}
      {open?.dialog === 'abandon' && selection.kind === 'draft' && (
        <AbandonDraftDialog projectId={projectId} draft={selection} onClose={() => setOpen(null)} />
      )}
      {open?.dialog === 'compare' && (
        <CompareDialog
          projectId={projectId}
          sides={[
            { kind: 'main' },
            ...(drafts.data ?? []).map((d): ModelSelection => ({ kind: 'draft', id: d.id, streamId: d.streamId, name: d.name })),
            ...(variants.data ?? []).map((v): ModelSelection => ({ kind: 'variant', id: v.id, streamId: v.streamId, name: v.name })),
            ...(revisions.data ?? []).map((r): ModelSelection => ({ kind: 'revision', id: r.id, name: r.name })),
          ]}
          initial={selection}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  )
}
