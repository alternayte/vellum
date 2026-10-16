import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Lock, X } from 'lucide-react'
import { CanvasView } from '@/components/canvas/canvas-view'
import { StrataBreadcrumb } from '@/components/shell/strata-breadcrumb'
import { renderPage } from '@/components/docs/page-tree'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { canContain } from '@/lib/containment-rules'
import { factStatusColor, factStatusLabel } from '@/lib/fact-status'
import { cn } from '@/lib/utils'
import { readShare, unlockShare, type SharedPage, type SharedStep } from '@/lib/share-api'

type Tab = 'diagram' | 'contracts' | 'flows' | 'docs' | 'decisions' | 'promises' | 'costs'

const date = (iso: string) => new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })

/**
 * The read-only page of a share link, for a person with no account. `embed` drops everything
 * but the diagram and its details, for the frame of another site. Nothing here writes: the
 * canvas pans, zooms, selects and drills in, and that is all.
 */
export function ShareViewer({ token, embed = false }: { token: string; embed?: boolean }) {
  const queryClient = useQueryClient()
  const read = useQuery({
    queryKey: ['share', token],
    queryFn: () => readShare(token),
    retry: false,
    staleTime: 60_000,
    refetchOnWindowFocus: false,
  })

  // A shared page starts on the top level of its own model, never on a level a member left open.
  useEffect(() => {
    useCanvasStore.getState().reset()
    useShellStore.getState().reset()
  }, [token])

  useEffect(() => {
    const name = read.data?.kind === 'page' ? read.data.page.projectName : 'Shared architecture'
    document.title = `${name} · Vellum`
  }, [read.data])

  if (read.isLoading) return <Centered>Loading the shared page…</Centered>
  const result = read.data
  if (!result || result.kind === 'error') return <Centered>{result?.message ?? 'The shared page did not load. Try again later.'}</Centered>
  if (result.kind === 'gone') {
    return (
      <Centered>
        <p className="font-medium text-foreground">This link does not exist or no longer works.</p>
        <p className="mt-1">Ask the person who shared it for a new link.</p>
      </Centered>
    )
  }
  if (result.kind === 'password') {
    return <PasswordPrompt token={token} onUnlocked={() => queryClient.invalidateQueries({ queryKey: ['share', token] })} />
  }
  if (embed && result.page.scope !== 'view') {
    return <Centered>Only a link to one view can be embedded. Open the link in a browser tab instead.</Centered>
  }
  return <SharedWorkspace page={result.page} embed={embed} />
}

function Centered({ children }: { children: ReactNode }) {
  return (
    <main className="flex h-dvh items-center justify-center bg-background px-6 text-center text-sm text-muted-foreground">
      <div>{children}</div>
    </main>
  )
}

function PasswordPrompt({ token, onUnlocked }: { token: string; onUnlocked: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    const refused = await unlockShare(token, password)
    setBusy(false)
    if (refused) setError(refused)
    else onUnlocked()
  }

  return (
    <main className="flex h-dvh items-center justify-center bg-background px-6">
      <form onSubmit={submit} className="w-full max-w-sm rounded-lg border border-border bg-card p-6 shadow-sm">
        <div className="mb-4 flex items-center gap-2">
          <Lock className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
          <h1 className="font-heading text-base font-medium">This shared page needs a password</h1>
        </div>
        <label htmlFor="share-password" className="text-xs text-muted-foreground">Password</label>
        <Input
          id="share-password" type="password" autoComplete="off" autoFocus value={password}
          onChange={(e) => setPassword(e.target.value)} className="mt-1"
          aria-invalid={error ? true : undefined} aria-describedby={error ? 'share-password-error' : undefined}
        />
        {error && <p id="share-password-error" role="alert" className="mt-2 text-xs text-destructive">{error}</p>}
        <Button type="submit" className="mt-4 w-full" disabled={busy || password.length === 0}>{busy ? 'Checking…' : 'Open'}</Button>
      </form>
    </main>
  )
}

function ModeLabel({ page }: { page: SharedPage }) {
  return page.mode === 'pinned' && page.revision ? (
    <span className="rounded-full border border-border px-2 py-0.5 text-[11px] text-muted-foreground" title="This page shows one named revision. It does not change.">
      Pinned · revision <span className="text-foreground">{page.revision.name}</span> · {date(page.revision.createdAt)}
      {page.scope === 'project' && <> · flows, docs and decisions as of {date(page.asOf)}</>}
    </span>
  ) : (
    <span className="rounded-full border border-border px-2 py-0.5 text-[11px] text-muted-foreground" title="This page follows the accepted model. Reload to see the latest.">
      Live · accepted model as of {date(page.asOf)}
    </span>
  )
}

function SharedWorkspace({ page, embed }: { page: SharedPage; embed: boolean }) {
  const [tab, setTab] = useState<Tab>('diagram')
  const [viewId, setViewId] = useState<string | null>(page.views[0]?.id ?? null)
  const tabs: { id: Tab; label: string }[] = [
    { id: 'diagram', label: 'Diagram' },
    { id: 'contracts', label: 'Contracts' },
    ...(page.scope === 'project'
      ? [
          { id: 'flows' as const, label: `Flows (${page.flows.length})` },
          { id: 'docs' as const, label: `Docs (${page.docs.length})` },
          { id: 'decisions' as const, label: `Decisions (${page.decisions.length})` },
        ]
      : []),
    ...(page.promises ? [{ id: 'promises' as const, label: `Promises (${page.promises.length})` }] : []),
    ...(page.costs ? [{ id: 'costs' as const, label: 'Costs' }] : []),
  ]
  const view = page.views.find((v) => v.id === viewId) ?? null

  return (
    <div className="flex h-dvh flex-col bg-background text-foreground">
      <header className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-border bg-card px-3', embed ? 'py-1.5' : 'py-2')}>
        <h1 className="min-w-0 truncate text-sm font-medium">{page.projectName}{view && page.scope === 'view' ? ` · ${view.name}` : ''}</h1>
        <ModeLabel page={page} />
        <span className="text-[11px] text-muted-foreground">Read-only</span>
        {!embed && (
          <nav aria-label="Shared page sections" className="ml-auto flex gap-1 overflow-x-auto">
            {tabs.map((t) => (
              <button
                key={t.id} type="button" onClick={() => setTab(t.id)} aria-current={tab === t.id ? 'page' : undefined}
                className={cn('whitespace-nowrap rounded-md px-2.5 py-1 text-xs text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary',
                  tab === t.id && 'bg-muted text-foreground')}
              >
                {t.label}
              </button>
            ))}
          </nav>
        )}
      </header>

      {tab === 'diagram' || embed ? (
        <Diagram page={page} view={view} views={page.views} onView={setViewId} />
      ) : (
        <main className="min-h-0 flex-1 overflow-y-auto px-4 py-6 md:px-8">
          <div className="mx-auto max-w-4xl space-y-6">
            {tab === 'contracts' && <Contracts page={page} />}
            {tab === 'flows' && <Flows page={page} />}
            {tab === 'docs' && <Docs page={page} />}
            {tab === 'decisions' && <Decisions page={page} />}
            {tab === 'promises' && page.promises && <Promises page={page} />}
            {tab === 'costs' && page.costs && <Costs page={page} />}
          </div>
        </main>
      )}
    </div>
  )
}

function Diagram({ page, view, views, onView }: { page: SharedPage; view: SharedPage['views'][number] | null; views: SharedPage['views']; onView: (id: string) => void }) {
  const elements = useMemo(() => page.model.elements.map((e) => ({
    id: e.id, kind: e.kind, name: e.name, description: e.description, technology: e.technology, status: e.status,
    tags: e.tags, ownerId: null, parentId: e.parentId, icon: e.icon, factStatus: e.truthLabel,
    stale: e.stale ? { sinceRevision: '', reason: 'evidence-not-found' } : null,
    provenance: null,
  })), [page])
  const relationships = useMemo(() => page.model.relationships.map((r) => ({
    id: r.id, fromId: r.fromId, toId: r.toId, label: r.label, technology: r.technology, lineShape: r.lineShape, kind: r.kind, factStatus: r.truthLabel, provenance: null,
  })), [page])
  const positions = useMemo(() => (view?.positions ?? []).map((p) => ({
    elementId: p.elementId, x: p.x, y: p.y, width: p.width ?? undefined, height: p.height ?? undefined,
  })), [view])
  const selectedElementId = useShellStore((s) => s.selectedElementId)
  const selectedRelationshipId = useShellStore((s) => s.selectedRelationshipId)

  return (
    <div className="flex min-h-0 flex-1">
      <main className="relative min-w-0 flex-1">
        <div className="absolute left-3 top-2 z-10 flex max-w-[calc(100%-1.5rem)] items-center gap-2 rounded-md border border-border bg-card/95 px-2 py-1 text-xs shadow-sm backdrop-blur">
          {views.length > 1 && page.scope === 'project' && (
            <select aria-label="Diagram" value={view?.id ?? ''} onChange={(e) => onView(e.target.value)} className="rounded border border-border bg-background px-1.5 py-0.5 text-xs">
              {views.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}
            </select>
          )}
          <StrataBreadcrumb />
        </div>
        <CanvasView
          readOnly
          elements={elements}
          relationships={relationships}
          positions={positions}
          edgeSides={view?.edges ?? undefined}
          messages={page.model.messages}
          onNodeDoubleClick={(id) => {
            const el = page.model.elements.find((e) => e.id === id)
            if (el && canContain(el.kind)) useCanvasStore.getState().drillInto({ elementId: el.id, name: el.name, kind: el.kind })
          }}
        />
      </main>
      {(selectedElementId || selectedRelationshipId) && (
        <Details page={page} elementId={selectedElementId} relationshipId={selectedRelationshipId} />
      )}
    </div>
  )
}

function Truth({ label, citation, stale }: { label: string; citation: string | null; stale: boolean }) {
  return (
    <div className="space-y-1 text-xs">
      <span className="inline-flex items-center gap-1.5 text-foreground">
        <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: factStatusColor(label) }} />
        {factStatusLabel(label)}
      </span>
      {citation && <p className="font-mono text-[11px] text-muted-foreground" title="Where the evidence is. The source text is not shared.">{citation}</p>}
      {stale && <p className="text-[11px] text-[var(--trace-retry)]">Stale: the latest complete scan did not find the evidence.</p>}
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm text-foreground">{children}</dd>
    </div>
  )
}

function Details({ page, elementId, relationshipId }: { page: SharedPage; elementId: string | null; relationshipId: string | null }) {
  const model = page.model
  const name = (id: string | null) => (id ? model.elements.find((e) => e.id === id)?.name ?? 'Not shared' : '—')
  const schema = (id: string | null) => (id ? page.schemas.find((s) => s.id === id)?.name ?? 'Unknown schema' : null)
  const close = () => {
    useShellStore.getState().selectElement(null)
    useShellStore.getState().selectRelationship(null)
  }
  const element = elementId ? model.elements.find((e) => e.id === elementId) : null
  const relationship = !element && relationshipId ? model.relationships.find((r) => r.id === relationshipId) : null
  if (!element && !relationship) return null

  return (
    <aside aria-label="Details" className="w-80 max-w-[85vw] shrink-0 overflow-y-auto border-l border-border bg-card p-4">
      <div className="mb-3 flex items-start justify-between gap-2">
        <h2 className="text-sm font-medium">{element ? element.name : `${name(relationship!.fromId)} → ${name(relationship!.toId)}`}</h2>
        <button type="button" onClick={close} aria-label="Close details" className="rounded p-0.5 text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">
          <X className="h-4 w-4" />
        </button>
      </div>
      {element && (
        <dl className="space-y-3">
          <Field label="Kind">{element.kind}{element.technology ? ` · ${element.technology}` : ''} · {element.status}</Field>
          {element.description && <Field label="Description"><span className="whitespace-pre-wrap">{element.description}</span></Field>}
          <Field label="Truth"><Truth label={element.truthLabel} citation={element.citation} stale={element.stale} /></Field>
          {element.tags.length > 0 && <Field label="Tags">{element.tags.join(', ')}</Field>}
          <ListField label="Operations" items={model.operations.filter((o) => o.ownerId === element.id).map((o) => (
            <>
              <span className="font-mono text-xs">{o.transport.toUpperCase()} {o.name}</span>
              {(o.requestSchemaId || o.responseSchemaId) && (
                <span className="block text-[11px] text-muted-foreground">
                  {schema(o.requestSchemaId) ? `request ${schema(o.requestSchemaId)}` : ''}{o.requestSchemaId && o.responseSchemaId ? ' · ' : ''}{schema(o.responseSchemaId) ? `response ${schema(o.responseSchemaId)}` : ''}
                </span>
              )}
            </>
          ))} />
          <ListField label="Publishes" items={model.messages.filter((m) => m.producerId === element.id).map((m) => <>{m.name}{schema(m.schemaId) ? <span className="text-muted-foreground"> · {schema(m.schemaId)}</span> : null}</>)} />
          <ListField label="Consumes" items={model.messages.filter((m) => m.consumerIds.includes(element.id)).map((m) => <>{m.name}</>)} />
          <ListField label="Relationships" items={model.relationships.filter((r) => r.fromId === element.id || r.toId === element.id).map((r) => (
            <>{r.fromId === element.id ? `→ ${name(r.toId)}` : `← ${name(r.fromId)}`}{r.label ? <span className="text-muted-foreground"> · {r.label}</span> : null}</>
          ))} />
          {page.costs && (
            <ListField label="Estimated monthly cost" items={page.costs.lines.filter((l) => l.elementId === element.id).map((l) => (
              <>{l.resource} · {l.name}: {l.amount !== null ? `${l.amount.toFixed(2)} ${l.currency}` : <span className="text-muted-foreground">unknown ({l.reason ?? l.status})</span>}</>
            ))} />
          )}
        </dl>
      )}
      {relationship && (
        <dl className="space-y-3">
          {relationship.label && <Field label="Label">{relationship.label}</Field>}
          {relationship.technology && <Field label="Technology">{relationship.technology}</Field>}
          <Field label="Kind">{relationship.kind}</Field>
          <Field label="Truth"><Truth label={relationship.truthLabel} citation={relationship.citation} stale={relationship.stale} /></Field>
        </dl>
      )}
    </aside>
  )
}

function ListField({ label, items }: { label: string; items: ReactNode[] }) {
  if (items.length === 0) return null
  return (
    <Field label={label}>
      <ul className="space-y-1">{items.map((item, i) => <li key={i}>{item}</li>)}</ul>
    </Field>
  )
}

function Section({ title, empty, children }: { title: string; empty?: string | false; children?: ReactNode }) {
  return (
    <section>
      <h2 className="mb-2 font-heading text-sm font-medium">{title}</h2>
      {empty ? <p className="text-sm text-muted-foreground">{empty}</p> : children}
    </section>
  )
}

function Contracts({ page }: { page: SharedPage }) {
  const model = page.model
  const owner = (id: string) => model.elements.find((e) => e.id === id)?.name ?? 'Not shared'
  const channel = (id: string) => model.channels.find((c) => c.id === id)?.name ?? 'Not shared'
  const schemaName = (id: string | null) => (id ? page.schemas.find((s) => s.id === id)?.name ?? null : null)
  return (
    <>
      <Section title="Operations" empty={model.operations.length === 0 && 'No operation is shared.'}>
        <ul className="divide-y divide-border rounded-md border border-border">
          {model.operations.map((o) => (
            <li key={o.id} className="flex flex-wrap items-baseline justify-between gap-2 px-3 py-2 text-sm">
              <span><span className="font-mono text-xs">{o.transport.toUpperCase()} {o.name}</span> <span className="text-muted-foreground">· {owner(o.ownerId)}</span></span>
              <span className="text-xs text-muted-foreground">
                {[schemaName(o.requestSchemaId) && `request ${schemaName(o.requestSchemaId)}`, schemaName(o.responseSchemaId) && `response ${schemaName(o.responseSchemaId)}`,
                  o.timeoutMs > 0 && `timeout ${o.timeoutMs} ms`, o.retry.maxAttempts > 1 && `${o.retry.maxAttempts} attempts`].filter(Boolean).join(' · ')}
              </span>
              <span className="w-full"><Truth label={o.truthLabel} citation={o.citation} stale={o.stale} /></span>
            </li>
          ))}
        </ul>
      </Section>
      <Section title="Channels and messages" empty={model.channels.length + model.messages.length === 0 && 'No channel or message is shared.'}>
        <ul className="divide-y divide-border rounded-md border border-border">
          {model.channels.map((c) => (
            <li key={c.id} className="px-3 py-2 text-sm">
              {c.kind} <span className="font-medium">{c.name}</span>
              <span className="text-xs text-muted-foreground"> · consumers: {model.subscriptions.filter((s) => s.channelId === c.id).map((s) => owner(s.consumerId)).join(', ') || 'none'}</span>
            </li>
          ))}
          {model.messages.map((m) => (
            <li key={m.id} className="px-3 py-2 text-sm">
              message <span className="font-medium">{m.name}</span>
              <span className="text-xs text-muted-foreground"> · from {owner(m.producerId)}{m.consumerIds.length > 0 ? ` to ${m.consumerIds.map(owner).join(', ')}` : ''}{schemaName(m.schemaId) ? ` · schema ${schemaName(m.schemaId)}` : ''}</span>
            </li>
          ))}
          {model.bindings.map((b) => (
            <li key={b.id} className="px-3 py-2 text-xs text-muted-foreground">binding {channel(b.sourceChannelId)} → {channel(b.targetChannelId)}{b.routingKey ? ` (${b.routingKey})` : ''}</li>
          ))}
        </ul>
      </Section>
      <Section title="Payload schemas" empty={page.schemas.length === 0 && 'No schema is shared.'}>
        <div className="space-y-3">
          {page.schemas.map((s) => (
            <details key={s.id} className="rounded-md border border-border">
              <summary className="cursor-pointer px-3 py-2 text-sm">{s.name} <span className="text-xs text-muted-foreground">v{s.version}</span></summary>
              {s.description && <p className="px-3 text-xs text-muted-foreground">{s.description}</p>}
              <pre className="max-h-96 overflow-auto px-3 py-2 font-mono text-[11px]">{s.content}</pre>
            </details>
          ))}
        </div>
      </Section>
    </>
  )
}

function Flows({ page }: { page: SharedPage }) {
  const ids = useMemo(() => {
    const m = page.model
    return new Map<string, string>([
      ...m.elements.map((e) => [e.id, e.name] as const),
      ...m.operations.map((o) => [o.id, o.name] as const),
      ...m.channels.map((c) => [c.id, c.name] as const),
      ...m.messages.map((x) => [x.id, x.name] as const),
    ])
  }, [page])
  const n = (id: string | null) => (id ? ids.get(id) ?? 'not shared' : '—')

  const groupLabel: Record<string, string | null> = { then: null, 'on-error': 'On error:', else: 'Else:', branch: 'Branch:', handler: 'A consumer runs:' }
  const Steps = ({ steps }: { steps: SharedStep[] }): ReactNode => (
    <ol className="ml-4 list-decimal space-y-1 text-sm">
      {steps.map((s) => (
        <li key={s.id}>
          {s.type === 'call' && <>{n(s.fromId)} calls <span className="font-mono text-xs">{n(s.targetId)}</span></>}
          {s.type === 'publish' && <>{n(s.fromId)} publishes {n(s.targetId)} to {n(s.channelId)}</>}
          {s.type === 'write' && <>{n(s.fromId)} writes {s.dataset} in {n(s.targetId)}</>}
          {s.type === 'parallel' && <>In parallel:</>}
          {s.type === 'branch' && <>If step {s.whenStep} {s.is}:</>}
          {s.groups.map((g, i) => (
            <div key={i}>
              {groupLabel[g.name] && <span className="text-xs text-muted-foreground">{groupLabel[g.name]}</span>}
              <Steps steps={g.steps} />
            </div>
          ))}
        </li>
      ))}
    </ol>
  )

  return (
    <Section title="Flows" empty={page.flows.length === 0 && 'No flow is shared.'}>
      <div className="space-y-4">
        {page.flows.map((f) => (
          <article key={f.id} className="rounded-md border border-border p-3">
            <h3 className="mb-2 text-sm font-medium">{f.name}</h3>
            <Steps steps={f.steps} />
            {(f.faults > 0 || f.assertions > 0) && (
              <p className="mt-2 text-xs text-muted-foreground">{f.faults} faults · {f.assertions} assertions</p>
            )}
          </article>
        ))}
      </div>
    </Section>
  )
}

function Docs({ page }: { page: SharedPage }) {
  const [docId, setDocId] = useState(page.docs[0]?.id ?? null)
  const doc = page.docs.find((d) => d.id === docId)
  const [body, setBody] = useState<ReactNode>(null)
  useEffect(() => {
    let cancelled = false
    if (!doc) return
    const names = new Map(page.model.elements.map((e) => [e.id, e.name]))
    renderPage(doc.content, {
      ElementLink: ({ id }) => <span className="font-medium">{names.get(id) ?? 'an element that is not shared'}</span>,
      LiveView: ({ id }) => <span className="text-muted-foreground">Diagram: {page.views.find((v) => v.id === id)?.name ?? 'not shared'}</span>,
    }).then((node) => { if (!cancelled) setBody(node) }, () => { if (!cancelled) setBody(<pre className="whitespace-pre-wrap text-sm">{doc.content}</pre>) })
    return () => { cancelled = true }
  }, [doc, page])

  if (page.docs.length === 0) return <Section title="Docs" empty="No doc is shared." />
  return (
    <div className="flex flex-col gap-4 md:flex-row">
      <nav aria-label="Docs" className="shrink-0 md:w-56">
        <ul className="space-y-0.5">
          {page.docs.map((d) => (
            <li key={d.id}>
              <button type="button" onClick={() => setDocId(d.id)} aria-current={d.id === docId ? 'page' : undefined}
                className={cn('w-full truncate rounded px-2 py-1 text-left text-sm text-muted-foreground hover:bg-muted hover:text-foreground', d.id === docId && 'bg-muted text-foreground')}>
                {d.title}
              </button>
            </li>
          ))}
        </ul>
      </nav>
      {doc && (
        <article className="prose prose-sm prose-invert min-w-0 max-w-none flex-1">
          <h1>{doc.title}</h1>
          <p className="text-xs text-muted-foreground">{doc.space ? `${doc.space} · ` : ''}updated {date(doc.updatedAt)}</p>
          {body}
        </article>
      )}
    </div>
  )
}

function Decisions({ page }: { page: SharedPage }) {
  return (
    <Section title="Decisions" empty={page.decisions.length === 0 && 'No accepted decision is shared.'}>
      <div className="space-y-4">
        {page.decisions.map((d) => (
          <article key={d.id} className="rounded-md border border-border p-4 text-sm">
            <h3 className="font-medium"><span className="font-mono text-xs text-muted-foreground">{d.label}</span> {d.title}</h3>
            <p className="text-xs text-muted-foreground">{d.status}{d.acceptedAt ? ` · accepted ${date(d.acceptedAt)}` : ''}</p>
            <h4 className="mt-3 text-xs font-medium uppercase tracking-wide text-muted-foreground">Context</h4>
            <p className="whitespace-pre-wrap">{d.context}</p>
            <h4 className="mt-3 text-xs font-medium uppercase tracking-wide text-muted-foreground">Decision</h4>
            <p className="whitespace-pre-wrap">{d.decision}</p>
            {d.alternatives.length > 0 && (
              <>
                <h4 className="mt-3 text-xs font-medium uppercase tracking-wide text-muted-foreground">Alternatives</h4>
                <ul className="ml-4 list-disc">{d.alternatives.map((a, i) => <li key={i}>{a.title}{a.whyNot ? ` — ${a.whyNot}` : ''}</li>)}</ul>
              </>
            )}
            {d.consequences && (
              <>
                <h4 className="mt-3 text-xs font-medium uppercase tracking-wide text-muted-foreground">Consequences</h4>
                <p className="whitespace-pre-wrap">{d.consequences}</p>
              </>
            )}
          </article>
        ))}
      </div>
    </Section>
  )
}

function Promises({ page }: { page: SharedPage }) {
  return (
    <Section title="Promises" empty={page.promises!.length === 0 && 'No approved promise is shared.'}>
      <p className="mb-3 text-xs text-muted-foreground">A passed check covers what it checked. A check that did not run is no pass.</p>
      <div className="space-y-4">
        {page.promises!.map((p) => (
          <article key={p.requirementId} className="rounded-md border border-border p-4 text-sm">
            <h3 className="font-medium"><span className="font-mono text-xs text-muted-foreground">{p.requirementId}</span> {p.title}</h3>
            <p className="mt-1 whitespace-pre-wrap">{p.claim}</p>
            <p className="mt-2 text-xs text-muted-foreground">
              {p.counts.passed} passed · {p.counts.failed} failed · {p.counts.notRun} not run · {p.counts.unsupported} unsupported · {p.counts.stale} stale
            </p>
            {p.obligations.length > 0 && (
              <ul className="mt-2 space-y-1 text-xs">
                {p.obligations.map((o) => (
                  <li key={o.id}>
                    <span className="font-mono">{o.result}</span>{o.current === false ? ' (inputs changed)' : ''} · {o.title}
                    {o.at ? <span className="text-muted-foreground"> · {date(o.at)}{o.environment ? ` · ${o.environment}` : ''}</span> : null}
                  </li>
                ))}
              </ul>
            )}
          </article>
        ))}
      </div>
    </Section>
  )
}

function Costs({ page }: { page: SharedPage }) {
  const costs = page.costs!
  const owner = (id: string) => page.model.elements.find((e) => e.id === id)?.name ?? 'Not shared'
  return (
    <Section title={`Estimated monthly cost · as of ${costs.asOf}`}>
      <p className="mb-3 text-sm">
        {costs.totals.length === 0 ? 'No line has a known price.' : costs.totals.map((t) => `${t.amount.toFixed(2)} ${t.currency}`).join(' + ')}
        {costs.unknownLines > 0 && <span className="text-muted-foreground"> · {costs.unknownLines} lines with no known price are in no total</span>}
      </p>
      <div className="overflow-x-auto">
        <table className="w-full text-left text-xs">
          <thead className="text-muted-foreground">
            <tr><th className="py-1 pr-3 font-normal">Element</th><th className="pr-3 font-normal">Resource</th><th className="pr-3 font-normal">Line</th><th className="pr-3 font-normal">Provider</th><th className="text-right font-normal">Amount</th></tr>
          </thead>
          <tbody className="divide-y divide-border">
            {costs.lines.map((l, i) => (
              <tr key={i}>
                <td className="py-1 pr-3">{owner(l.elementId)}</td>
                <td className="pr-3">{l.resource}</td>
                <td className="pr-3">{l.name} <span className="text-muted-foreground">{l.category}</span></td>
                <td className="pr-3">{[l.provider, l.region].filter(Boolean).join(' · ')}</td>
                <td className="text-right">{l.amount !== null ? `${l.amount.toFixed(2)} ${l.currency}` : <span className="text-muted-foreground">unknown</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Section>
  )
}
