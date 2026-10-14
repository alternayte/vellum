import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, Copy, Share2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Toggle, selectClass } from '@/components/settings/fields'
import { api, ApiError, type WorkspaceSettings } from '@/lib/settings-api'
import { shownInline } from '@/lib/mutation-errors'
import { useViews } from '@/hooks/use-views'
import { usePersonName } from '@/hooks/use-person-name'
import { cn } from '@/lib/utils'
import { shareLinksApi, type ShareLink, type ShareLinkCreated, type ShareMode, type ShareScope } from '@/lib/share-api'

interface Revision { id: string; name: string; createdAt: string }

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : 'never')

/** The "Share" button of a project and its dialog. Shown to Editors and Owners only. */
export function ShareButton({ projectId, workspaceId, role }: { projectId: string; workspaceId: string; role: string | undefined }) {
  const [open, setOpen] = useState(false)
  if (role !== 'Editor' && role !== 'Owner') return null
  return (
    <>
      <Button variant="outline" size="sm" className="gap-1.5 text-xs" onClick={() => setOpen(true)}>
        <Share2 className="h-3.5 w-3.5" aria-hidden="true" />
        <span className="max-sm:hidden">Share</span>
      </Button>
      {open && <ShareDialog projectId={projectId} workspaceId={workspaceId} onClose={() => setOpen(false)} />}
    </>
  )
}

function ShareDialog({ projectId, workspaceId, onClose }: { projectId: string; workspaceId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const key = ['projects', projectId, 'share-links']
  const links = useQuery({ queryKey: key, queryFn: () => shareLinksApi.list(projectId) })
  const settings = useQuery({
    queryKey: ['workspaces', workspaceId, 'settings'],
    queryFn: () => api<WorkspaceSettings>('GET', `/api/workspaces/${workspaceId}/settings`),
  })
  const revisions = useQuery({ queryKey: ['projects', projectId, 'revisions'], queryFn: () => api<Revision[]>('GET', `/api/projects/${projectId}/revisions`) })
  const { data: views } = useViews(projectId)
  const personName = usePersonName(projectId)
  const policy = settings.data?.effective.sharing.linkPolicy

  const [scope, setScope] = useState<ShareScope>('project')
  const [viewId, setViewId] = useState('')
  const [mode, setMode] = useState<ShareMode>('live')
  const [revisionId, setRevisionId] = useState('')
  const [label, setLabel] = useState('')
  const [showCosts, setShowCosts] = useState(false)
  const [showPromises, setShowPromises] = useState(false)
  const [password, setPassword] = useState('')
  const [expiresAt, setExpiresAt] = useState('')
  const [created, setCreated] = useState<ShareLinkCreated | null>(null)

  const refresh = () => queryClient.invalidateQueries({ queryKey: key })
  const create = useMutation({
    mutationFn: () => shareLinksApi.create(projectId, {
      scope, viewId: scope === 'view' ? viewId || null : null, mode, revisionId: mode === 'pinned' ? revisionId || null : null,
      label, showCosts, showPromises, password: password || null,
      expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
    }),
    onSuccess: (result) => { setCreated(result); setPassword(''); refresh() },
    onError: shownInline,
  })
  const fieldError = (field: string) => create.error instanceof ApiError ? create.error.fields.find((f) => f.field === field)?.message : undefined
  const formError = create.error instanceof ApiError && create.error.fields.length === 0 ? create.error.message : null

  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose() }}>
      <DialogContent className="max-h-[90dvh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Share read-only</DialogTitle>
          <DialogDescription>
            A link shows diagrams, details with truth labels, contracts, flows, docs and accepted decisions to people without an account.
            It never shows drafts, proposals, unconfirmed inferences, source text, comments, members or settings.
          </DialogDescription>
        </DialogHeader>

        {policy === 'off' && <p role="status" className="rounded-md border border-border px-3 py-2 text-xs">The workspace link policy is off. An Owner can change it in the workspace settings. Existing links answer 404.</p>}
        {policy === 'password-only' && <p className="text-xs text-muted-foreground">The workspace link policy allows links with a password only.</p>}

        {created ? (
          <CreatedLink created={created} onDone={() => setCreated(null)} />
        ) : (
          <form className="grid gap-3 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); create.mutate() }}>
            <label className="text-xs text-muted-foreground">Scope
              <select className={cn(selectClass, 'mt-1 w-full')} value={scope} onChange={(e) => setScope(e.target.value as ShareScope)}>
                <option value="project">The whole project</option>
                <option value="view">One view (can be embedded)</option>
              </select>
            </label>
            {scope === 'view' ? (
              <label className="text-xs text-muted-foreground">View
                <select className={cn(selectClass, 'mt-1 w-full')} value={viewId} onChange={(e) => setViewId(e.target.value)} aria-invalid={fieldError('viewId') ? true : undefined}>
                  <option value="">Select a view</option>
                  {(views ?? []).map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}
                </select>
                {fieldError('viewId') && <span role="alert" className="mt-1 block text-destructive">{fieldError('viewId')}</span>}
              </label>
            ) : <span className="max-sm:hidden" />}
            <label className="text-xs text-muted-foreground">Mode
              <select className={cn(selectClass, 'mt-1 w-full')} value={mode} onChange={(e) => setMode(e.target.value as ShareMode)}>
                <option value="live">Live: follows the accepted model on main</option>
                <option value="pinned">Pinned: one named revision, never changes</option>
              </select>
            </label>
            {mode === 'pinned' ? (
              <label className="text-xs text-muted-foreground">Revision
                <select className={cn(selectClass, 'mt-1 w-full')} value={revisionId} onChange={(e) => setRevisionId(e.target.value)} aria-invalid={fieldError('revisionId') ? true : undefined}>
                  <option value="">{(revisions.data ?? []).length === 0 ? 'No named revision yet' : 'Select a revision'}</option>
                  {(revisions.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name} · {new Date(r.createdAt).toLocaleDateString()}</option>)}
                </select>
                {fieldError('revisionId') && <span role="alert" className="mt-1 block text-destructive">{fieldError('revisionId')}</span>}
              </label>
            ) : <span className="max-sm:hidden" />}
            <label className="text-xs text-muted-foreground">Password {policy === 'password-only' ? '(required)' : '(optional)'}
              <Input type="password" autoComplete="new-password" className="mt-1" value={password} onChange={(e) => setPassword(e.target.value)} aria-invalid={fieldError('password') ? true : undefined} />
              {fieldError('password') && <span role="alert" className="mt-1 block text-destructive">{fieldError('password')}</span>}
            </label>
            <label className="text-xs text-muted-foreground">Expires (optional)
              <Input type="datetime-local" className="mt-1" value={expiresAt} onChange={(e) => setExpiresAt(e.target.value)} aria-invalid={fieldError('expiresAt') ? true : undefined} />
              {fieldError('expiresAt') && <span role="alert" className="mt-1 block text-destructive">{fieldError('expiresAt')}</span>}
            </label>
            <label className="text-xs text-muted-foreground sm:col-span-2">Name for the members (visitors do not see it)
              <Input className="mt-1" value={label} onChange={(e) => setLabel(e.target.value)} placeholder="For example: Partner review, Q4" />
            </label>
            <div className="flex items-center justify-between gap-3 text-sm sm:col-span-2">
              <span>Show estimated costs</span>
              <Toggle checked={showCosts} onChange={setShowCosts} label="Show estimated costs" />
            </div>
            <div className="flex items-center justify-between gap-3 text-sm sm:col-span-2">
              <span>Show promises with their verification status</span>
              <Toggle checked={showPromises} onChange={setShowPromises} label="Show promises with their verification status" />
            </div>
            {formError && <p role="alert" className="text-xs text-destructive sm:col-span-2">{formError}</p>}
            <div className="flex justify-end sm:col-span-2">
              <Button type="submit" disabled={create.isPending || policy === 'off'}>{create.isPending ? 'Creating…' : 'Create link'}</Button>
            </div>
          </form>
        )}

        <section aria-label="Links of this project" className="mt-2">
          <h3 className="mb-2 text-sm font-medium">Links</h3>
          {links.isLoading && <p className="text-xs text-muted-foreground">Loading…</p>}
          {links.data?.length === 0 && <p className="text-xs text-muted-foreground">No link yet.</p>}
          {(links.data?.length ?? 0) > 0 && (
            <ul className="divide-y divide-border rounded-md border border-border">
              {links.data?.map((link) => (
                <LinkRow key={link.id} projectId={projectId} link={link} createdBy={personName(link.createdBy)} onChanged={refresh} />
              ))}
            </ul>
          )}
        </section>
      </DialogContent>
    </Dialog>
  )
}

function CopyField({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <label className="block text-xs text-muted-foreground">{label}
      <span className="mt-1 flex gap-2">
        <Input readOnly value={value} onFocus={(e) => e.target.select()} className="font-mono text-xs" />
        <Button type="button" variant="outline" size="sm" aria-label={`Copy ${label.toLowerCase()}`} onClick={async () => {
          try {
            await navigator.clipboard.writeText(value)
            setCopied(true)
          } catch {
            setCopied(false)
          }
        }}>
          {copied ? <Check className="h-3.5 w-3.5" /> : <Copy className="h-3.5 w-3.5" />}
        </Button>
      </span>
    </label>
  )
}

function CreatedLink({ created, onDone }: { created: ShareLinkCreated; onDone: () => void }) {
  const origin = window.location.origin
  const embed = created.embedPath ? `${origin}${created.embedPath}` : null
  return (
    <div className="space-y-3 rounded-md border border-border p-3">
      <p className="text-sm">The link is ready. Copy it now: Vellum keeps only a hash of it and cannot show it again.</p>
      <CopyField label="Link" value={`${origin}${created.path}`} />
      {embed && <CopyField label="Embed code" value={`<iframe src="${embed}" width="100%" height="600" style="border:0" title="${created.link.viewName ?? 'Architecture view'}"></iframe>`} />}
      {embed && <p className="text-xs text-muted-foreground">A site can show the embed only when an Owner adds its origin to the embed allow-list in the workspace settings.</p>}
      <div className="flex justify-end"><Button type="button" variant="outline" size="sm" onClick={onDone}>Create another link</Button></div>
    </div>
  )
}

function LinkRow({ projectId, link, createdBy, onChanged }: { projectId: string; link: ShareLink; createdBy: string; onChanged: () => void }) {
  const [error, setError] = useState<string | null>(null)
  const done = { onSuccess: () => { setError(null); onChanged() }, onError: (e: Error) => setError(e.message) }
  const change = useMutation({ mutationFn: (body: Parameters<typeof shareLinksApi.change>[2]) => shareLinksApi.change(projectId, link.id, body), ...done })
  const revoke = useMutation({ mutationFn: () => shareLinksApi.revoke(projectId, link.id), ...done })
  const active = link.status !== 'revoked'
  const manage = link.canManage && active

  return (
    <li className="space-y-1.5 px-3 py-2 text-sm">
      <div className="flex flex-wrap items-baseline gap-x-2">
        <span className="font-medium">{link.label || (link.scope === 'view' ? `View: ${link.viewName ?? 'deleted view'}` : 'Whole project')}</span>
        <span className={cn('rounded-full border px-1.5 text-[10px]', link.status === 'active' ? 'border-primary/50 text-primary' : 'border-border text-muted-foreground')}>{link.status}</span>
        <span className="text-xs text-muted-foreground">
          {link.mode === 'pinned' ? `pinned to ${link.revisionName}` : 'live'} · {link.hasPassword ? 'password' : 'public'}
          {link.scope === 'view' && link.label ? ` · view ${link.viewName ?? 'deleted'}` : ''}
        </span>
      </div>
      <p className="text-xs text-muted-foreground">
        By {createdBy} · {when(link.createdAt)} · expires {when(link.expiresAt)} · last opened {link.lastAccessedAt ? when(link.lastAccessedAt) : 'never'}
        {link.status === 'blocked' && ' · the workspace link policy does not allow this link now, so it answers 404'}
      </p>
      {manage && (
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
          <span className="flex items-center gap-1.5">Costs <Toggle checked={link.showCosts} onChange={(v) => change.mutate({ showCosts: v })} label="Show estimated costs" /></span>
          <span className="flex items-center gap-1.5">Promises <Toggle checked={link.showPromises} onChange={(v) => change.mutate({ showPromises: v })} label="Show promises" /></span>
          {link.hasPassword && (
            <Button type="button" variant="ghost" size="sm" className="h-6 px-2 text-xs" onClick={() => change.mutate({ removePassword: true })}>Remove password</Button>
          )}
          <Button type="button" variant="ghost" size="sm" className="h-6 px-2 text-xs" onClick={() => {
            const password = window.prompt('New password for this link (at least 8 characters). Everyone who opened it types the new one.')
            if (password) change.mutate({ password })
          }}>{link.hasPassword ? 'Change password' : 'Set password'}</Button>
          {link.expiresAt && <Button type="button" variant="ghost" size="sm" className="h-6 px-2 text-xs" onClick={() => change.mutate({ clearExpiry: true })}>Remove expiry</Button>}
          <Button type="button" variant="ghost" size="sm" className="ml-auto h-6 px-2 text-xs text-destructive" onClick={() => {
            if (window.confirm('Revoke this link? Everyone who has it gets "not found" from now on.')) revoke.mutate()
          }}>Revoke</Button>
        </div>
      )}
      {error && <p role="alert" className="text-xs text-destructive">{error}</p>}
    </li>
  )
}
