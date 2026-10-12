import { useState, type FormEvent, type ReactNode } from 'react'
import { Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useChannels, useContractMutations, useOperations, useSubscriptions } from '@/hooks/use-contracts'

interface NamedElement {
  id: string
  name: string
}

interface ContractsFlyoutProps {
  projectId: string
  branchId?: string | null
  elements: NamedElement[]
}

const selectClass =
  'h-8 w-full rounded-lg border border-input bg-transparent px-2 text-sm text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50'

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block text-[11px] font-medium text-muted-foreground">
      {label}
      <span className="mt-1 block">{children}</span>
    </label>
  )
}

function Section({
  title, count, hint, adding, onToggle, children,
}: { title: string; count: number; hint: string; adding: boolean; onToggle: () => void; children: ReactNode }) {
  return (
    <section className="border-b border-border px-3 py-3 last:border-b-0">
      <div className="flex items-center justify-between">
        <h3 className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          {title} <span className="font-mono text-muted-foreground/70">{count}</span>
        </h3>
        <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={onToggle} aria-expanded={adding}>
          <Plus className="h-3 w-3" />
          {adding ? 'Cancel' : 'Add'}
        </Button>
      </div>
      {count === 0 && !adding && <p className="mt-1.5 text-xs leading-relaxed text-muted-foreground">{hint}</p>}
      {children}
    </section>
  )
}

function Row({ title, detail, onRemove, removeLabel }: { title: string; detail: string; onRemove: () => void; removeLabel: string }) {
  return (
    <li className="group flex items-start gap-2 rounded-md px-1.5 py-1 hover:bg-muted">
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm text-foreground">{title}</p>
        <p className="truncate font-mono text-[10px] text-muted-foreground">{detail}</p>
      </div>
      <button
        type="button"
        onClick={onRemove}
        aria-label={removeLabel}
        className="mt-0.5 rounded p-1 text-muted-foreground opacity-0 hover:text-[var(--trace-failure)] focus-visible:opacity-100 focus-visible:outline-2 focus-visible:outline-primary group-hover:opacity-100"
      >
        <Trash2 className="h-3.5 w-3.5" />
      </button>
    </li>
  )
}

/**
 * Contracts are what a scenario may reference: operations a service offers, channels that
 * carry messages, and the subscriptions that deliver them.
 */
export function ContractsFlyout({ projectId, branchId, elements }: ContractsFlyoutProps) {
  const { data: operations = [] } = useOperations(projectId, branchId)
  const { data: channels = [] } = useChannels(projectId, branchId)
  const { data: subscriptions = [] } = useSubscriptions(projectId, branchId)
  const mutations = useContractMutations(projectId, branchId)

  const [adding, setAdding] = useState<'operation' | 'channel' | 'subscription' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const nameOf = (id: string) => elements.find((e) => e.id === id)?.name ?? 'Unknown element'
  const channelName = (id: string) => channels.find((c) => c.id === id)?.name ?? 'Unknown channel'
  const toggle = (kind: typeof adding) => { setError(null); setAdding(adding === kind ? null : kind) }
  const done = { onSuccess: () => { setAdding(null); setError(null) }, onError: (e: Error) => setError(e.message) }
  const removed = { onError: (e: Error) => setError(e.message) }

  const submitOperation = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    mutations.addOperation.mutate({
      ownerId: String(form.get('ownerId')),
      name: String(form.get('name')).trim(),
      transport: String(form.get('transport')),
      timeoutMs: Number(form.get('timeoutMs')),
      retry: { maxAttempts: Number(form.get('maxAttempts')), backoffMs: Number(form.get('backoffMs')) },
    }, done)
  }

  const submitChannel = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    mutations.addChannel.mutate({ name: String(form.get('name')).trim(), kind: String(form.get('kind')) }, done)
  }

  const submitSubscription = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    mutations.addSubscription.mutate({
      channelId: String(form.get('channelId')),
      consumerId: String(form.get('consumerId')),
      retry: { maxAttempts: Number(form.get('maxAttempts')), backoffMs: Number(form.get('backoffMs')) },
      deadLetterChannelId: String(form.get('deadLetterChannelId')) || null,
    }, done)
  }

  const retryFields = (
    <div className="grid grid-cols-2 gap-2">
      <Field label="Attempts"><Input name="maxAttempts" type="number" min={1} max={10} defaultValue={1} required /></Field>
      <Field label="Backoff (ms)"><Input name="backoffMs" type="number" min={0} defaultValue={0} required /></Field>
    </div>
  )

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-border px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">Contracts</span>
      </div>
      {error && (
        <p role="alert" className="border-b border-border bg-[color-mix(in_srgb,var(--trace-failure)_12%,transparent)] px-3 py-2 text-xs text-foreground">
          {error}
        </p>
      )}
      <div className="flex-1 overflow-y-auto">
        <Section
          title="Operations" count={operations.length} adding={adding === 'operation'} onToggle={() => toggle('operation')}
          hint="An operation is a call a service accepts, such as POST /orders. Scenarios call operations."
        >
          {adding === 'operation' && (
            <form onSubmit={submitOperation} className="mt-2 space-y-2">
              <Field label="Name"><Input name="name" placeholder="POST /orders" required autoFocus /></Field>
              <Field label="Offered by">
                <select name="ownerId" className={selectClass} required>
                  {elements.map((el) => <option key={el.id} value={el.id}>{el.name}</option>)}
                </select>
              </Field>
              <div className="grid grid-cols-2 gap-2">
                <Field label="Transport">
                  <select name="transport" className={selectClass} defaultValue="http">
                    <option value="http">HTTP</option>
                    <option value="grpc">gRPC</option>
                    <option value="command">Command</option>
                  </select>
                </Field>
                <Field label="Timeout (ms)"><Input name="timeoutMs" type="number" min={0} defaultValue={1000} required /></Field>
              </div>
              {retryFields}
              <Button type="submit" size="sm" className="w-full" disabled={mutations.addOperation.isPending || elements.length === 0}>Add operation</Button>
            </form>
          )}
          <ul className="mt-1.5 space-y-0.5">
            {operations.map((op) => (
              <Row
                key={op.id} title={op.name}
                detail={`${nameOf(op.ownerId)} · ${op.transport} · ${op.timeoutMs} ms · ${op.retry.maxAttempts}×`}
                removeLabel={`Remove operation ${op.name}`}
                onRemove={() => mutations.removeOperation.mutate(op.id, removed)}
              />
            ))}
          </ul>
        </Section>

        <Section
          title="Channels" count={channels.length} adding={adding === 'channel'} onToggle={() => toggle('channel')}
          hint="A channel is a topic, queue or exchange. Scenarios publish messages to channels."
        >
          {adding === 'channel' && (
            <form onSubmit={submitChannel} className="mt-2 space-y-2">
              <Field label="Name"><Input name="name" placeholder="order-events" required autoFocus /></Field>
              <Field label="Kind">
                <select name="kind" className={selectClass} defaultValue="topic">
                  <option value="topic">Topic</option>
                  <option value="queue">Queue</option>
                  <option value="exchange">Exchange</option>
                </select>
              </Field>
              <Button type="submit" size="sm" className="w-full" disabled={mutations.addChannel.isPending}>Add channel</Button>
            </form>
          )}
          <ul className="mt-1.5 space-y-0.5">
            {channels.map((channel) => (
              <Row
                key={channel.id} title={channel.name} detail={channel.kind}
                removeLabel={`Remove channel ${channel.name}`}
                onRemove={() => mutations.removeChannel.mutate(channel.id, removed)}
              />
            ))}
          </ul>
        </Section>

        <Section
          title="Subscriptions" count={subscriptions.length} adding={adding === 'subscription'} onToggle={() => toggle('subscription')}
          hint={channels.length === 0 ? 'Add a channel first. A subscription delivers its messages to one consumer.' : 'A subscription delivers a channel’s messages to one consumer, with its own retries and dead-letter channel.'}
        >
          {adding === 'subscription' && (
            <form onSubmit={submitSubscription} className="mt-2 space-y-2">
              <Field label="Channel">
                <select name="channelId" className={selectClass} required>
                  {channels.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
              <Field label="Consumer">
                <select name="consumerId" className={selectClass} required>
                  {elements.map((el) => <option key={el.id} value={el.id}>{el.name}</option>)}
                </select>
              </Field>
              {retryFields}
              <Field label="Dead-letter channel">
                <select name="deadLetterChannelId" className={selectClass} defaultValue="">
                  <option value="">None: a failed message is lost</option>
                  {channels.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
              <Button type="submit" size="sm" className="w-full" disabled={mutations.addSubscription.isPending || channels.length === 0 || elements.length === 0}>Add subscription</Button>
            </form>
          )}
          <ul className="mt-1.5 space-y-0.5">
            {subscriptions.map((sub) => (
              <Row
                key={sub.id} title={`${nameOf(sub.consumerId)} on ${channelName(sub.channelId)}`}
                detail={`${sub.retry.maxAttempts}× · ${sub.deadLetterChannelId ? `DLQ ${channelName(sub.deadLetterChannelId)}` : 'no DLQ'}`}
                removeLabel={`Remove subscription of ${nameOf(sub.consumerId)}`}
                onRemove={() => mutations.removeSubscription.mutate(sub.id, removed)}
              />
            ))}
          </ul>
        </Section>
      </div>
    </div>
  )
}
