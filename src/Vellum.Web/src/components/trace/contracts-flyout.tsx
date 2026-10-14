import { useMemo, useState, type FormEvent } from 'react'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  NO_FAILURE, canRouteFrom, canRouteTo, formFailure, useBindings, useChannels, useContractMutations, useOperations, useSubscriptions,
  type FormFailure, type NewOperation, type Operation,
} from '@/hooks/use-contracts'
import { useMessages } from '@/hooks/use-messages'
import { useSchemas, type Schema } from '@/hooks/use-schemas'
import {
  Field, FormError, Row, SchemaSelect, Section, selectClass, type NamedElement,
} from './contract-parts'
import { MessagesSection } from './messages-section'
import { SchemasSection } from './schemas-section'

interface ContractsFlyoutProps {
  projectId: string
  branchId?: string | null
  elements: NamedElement[]
}

const OPERATION_FIELDS = ['name', 'ownerId', 'transport', 'requestSchemaId', 'responseSchemaId', 'timeoutMs']

interface OperationFormProps {
  operation?: Operation
  elements: NamedElement[]
  schemas: Schema[]
  /** True while the schema list has not loaded. */
  schemasLoading: boolean
  failure: FormFailure
  pending: boolean
  onSubmit: (operation: NewOperation & { requestSchemaId: string | null; responseSchemaId: string | null }) => void
  onCancel?: () => void
}

function OperationForm({ operation, elements, schemas, schemasLoading, failure, pending, onSubmit, onCancel }: OperationFormProps) {
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    onSubmit({
      ownerId: String(form.get('ownerId')),
      name: String(form.get('name')).trim(),
      transport: String(form.get('transport')),
      requestSchemaId: String(form.get('requestSchemaId')) || null,
      responseSchemaId: String(form.get('responseSchemaId')) || null,
      timeoutMs: Number(form.get('timeoutMs')),
      retry: { maxAttempts: Number(form.get('maxAttempts')), backoffMs: Number(form.get('backoffMs')) },
    })
  }

  return (
    <form onSubmit={submit} className="mt-2 space-y-2">
      <Field label="Name" error={failure.fields.name}>
        {(invalid) => <Input name="name" defaultValue={operation?.name ?? ''} placeholder="POST /orders" required autoFocus {...invalid} />}
      </Field>
      <Field label="Offered by" error={failure.fields.ownerId}>
        {(invalid) => (
          <select name="ownerId" className={selectClass} defaultValue={operation?.ownerId} required {...invalid}>
            {elements.map((el) => <option key={el.id} value={el.id}>{el.name}</option>)}
          </select>
        )}
      </Field>
      <Field label="Request schema" error={failure.fields.requestSchemaId}>
        {(invalid) => (
          <SchemaSelect name="requestSchemaId" initial={operation?.requestSchemaId} schemas={schemas} loading={schemasLoading} {...invalid} />
        )}
      </Field>
      <Field label="Response schema" error={failure.fields.responseSchemaId}>
        {(invalid) => (
          <SchemaSelect name="responseSchemaId" initial={operation?.responseSchemaId} schemas={schemas} loading={schemasLoading} {...invalid} />
        )}
      </Field>
      {!schemasLoading && schemas.length === 0 && (
        <p className="text-[11px] leading-relaxed text-muted-foreground">No schema exists yet. Add one under Schemas, then choose it here.</p>
      )}
      <div className="grid grid-cols-2 gap-2">
        <Field label="Transport" error={failure.fields.transport}>
          {(invalid) => (
            <select name="transport" className={selectClass} defaultValue={operation?.transport ?? 'http'} {...invalid}>
              <option value="http">HTTP</option>
              <option value="grpc">gRPC</option>
              <option value="command">Command</option>
            </select>
          )}
        </Field>
        <Field label="Timeout (ms)" error={failure.fields.timeoutMs}>
          {(invalid) => <Input name="timeoutMs" type="number" min={0} defaultValue={operation?.timeoutMs ?? 1000} required {...invalid} />}
        </Field>
      </div>
      <div className="grid grid-cols-2 gap-2">
        <Field label="Attempts"><Input name="maxAttempts" type="number" min={1} max={10} defaultValue={operation?.retry.maxAttempts ?? 1} required /></Field>
        <Field label="Backoff (ms)"><Input name="backoffMs" type="number" min={0} defaultValue={operation?.retry.backoffMs ?? 0} required /></Field>
      </div>
      <FormError message={failure.message} />
      <div className="flex gap-2">
        <Button type="submit" size="sm" className="flex-1" disabled={pending || elements.length === 0}>{operation ? 'Save operation' : 'Add operation'}</Button>
        {onCancel && <Button type="button" variant="ghost" size="sm" onClick={onCancel}>Cancel</Button>}
      </div>
    </form>
  )
}

/**
 * Contracts are what a scenario may reference: operations a service offers, channels that
 * carry messages, the bindings that route a message from an exchange or a topic to another
 * channel, the subscriptions that deliver them, the messages themselves, and the schemas
 * that operations and messages name.
 */
export function ContractsFlyout({ projectId, branchId, elements }: ContractsFlyoutProps) {
  const { data: operations = [] } = useOperations(projectId, branchId)
  const { data: channels = [] } = useChannels(projectId, branchId)
  const { data: subscriptions = [] } = useSubscriptions(projectId, branchId)
  const { data: bindings = [] } = useBindings(projectId, branchId)
  const { data: messages = [] } = useMessages(projectId, branchId ?? undefined)
  const { data: schemas = [], isPending: schemasLoading } = useSchemas(projectId)
  const mutations = useContractMutations(projectId, branchId)

  const [adding, setAdding] = useState<'operation' | 'channel' | 'subscription' | null>(null)
  const [editingOperation, setEditingOperation] = useState<string | null>(null)
  const [operationFailure, setOperationFailure] = useState<FormFailure>(NO_FAILURE)
  // The exchange or topic that a binding is being added to.
  const [bindingFrom, setBindingFrom] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const nameOf = (id: string) => elements.find((e) => e.id === id)?.name ?? 'Unknown element'
  const channelName = (id: string) => channels.find((c) => c.id === id)?.name ?? 'Unknown channel'
  const schemaName = (id: string) => schemas.find((s) => s.id === id)?.name ?? 'schema not found'
  const toggle = (kind: typeof adding) => {
    setError(null)
    setOperationFailure(NO_FAILURE)
    setEditingOperation(null)
    setAdding(adding === kind ? null : kind)
  }
  const done = { onSuccess: () => { setAdding(null); setError(null) }, onError: (e: Error) => setError(e.message) }
  const removed = { onError: (e: Error) => setError(e.message) }
  const operationDone = {
    onSuccess: () => { setAdding(null); setEditingOperation(null); setOperationFailure(NO_FAILURE) },
    onError: (e: Error) => setOperationFailure(formFailure(e, OPERATION_FIELDS)),
  }

  // What uses each schema on this branch. The server holds the rule for a removal; this says why before the click.
  const schemaUsers = useMemo(() => {
    const users = new Map<string, string[]>()
    const note = (schemaId: string | null, name: string) => {
      if (!schemaId) return
      const names = users.get(schemaId) ?? []
      if (!names.includes(name)) users.set(schemaId, [...names, name])
    }
    for (const operation of operations) {
      note(operation.requestSchemaId, operation.name)
      note(operation.responseSchemaId, operation.name)
    }
    for (const message of messages) note(message.schemaId, message.name)
    return users
  }, [operations, messages])

  const operationDetail = (op: Operation) => {
    const contract = [
      op.requestSchemaId ? `request ${schemaName(op.requestSchemaId)}` : null,
      op.responseSchemaId ? `response ${schemaName(op.responseSchemaId)}` : null,
    ].filter(Boolean).join(', ') || 'no schema'
    return `${nameOf(op.ownerId)} · ${op.transport} · ${op.timeoutMs} ms · ${op.retry.maxAttempts}× · ${contract}`
  }

  const submitChannel = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    mutations.addChannel.mutate({ name: String(form.get('name')).trim(), kind: String(form.get('kind')) }, done)
  }

  const submitBinding = (sourceChannelId: string) => (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    mutations.addBinding.mutate({
      sourceChannelId,
      targetChannelId: String(form.get('targetChannelId')),
      routingKey: String(form.get('routingKey')).trim() || null,
    }, { onSuccess: () => { setBindingFrom(null); setError(null) }, onError: (failure: Error) => setError(failure.message) })
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
            <OperationForm
              elements={elements} schemas={schemas} schemasLoading={schemasLoading} failure={operationFailure} pending={mutations.addOperation.isPending}
              onSubmit={(operation) => mutations.addOperation.mutate(operation, operationDone)}
            />
          )}
          <ul className="mt-1.5 space-y-0.5">
            {operations.map((op) => editingOperation === op.id ? (
              <li key={op.id} className="rounded-md border border-border px-2 pb-2">
                <OperationForm
                  operation={op} elements={elements} schemas={schemas} schemasLoading={schemasLoading} failure={operationFailure} pending={mutations.updateOperation.isPending}
                  onSubmit={(operation) => mutations.updateOperation.mutate({ id: op.id, ...operation }, operationDone)}
                  onCancel={() => { setEditingOperation(null); setOperationFailure(NO_FAILURE) }}
                />
              </li>
            ) : (
              <Row
                key={op.id} title={op.name} detail={operationDetail(op)}
                editLabel={`Edit operation ${op.name}`}
                onEdit={() => { setError(null); setAdding(null); setOperationFailure(NO_FAILURE); setEditingOperation(op.id) }}
                removeLabel={`Remove operation ${op.name}`}
                onRemove={() => mutations.removeOperation.mutate(op.id, removed)}
              />
            ))}
          </ul>
        </Section>

        <Section
          title="Channels" count={channels.length} adding={adding === 'channel'} onToggle={() => toggle('channel')}
          hint="A channel is a topic, queue or exchange. Scenarios publish messages to channels. A binding routes a message from an exchange or a topic to a queue or an exchange."
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
            {channels.map((channel) => {
              const routes = bindings.filter((b) => b.sourceChannelId === channel.id)
              const targets = channels.filter((c) => c.id !== channel.id && canRouteTo(c.kind))
              return (
                <li key={channel.id}>
                  <ul>
                    <Row
                      title={channel.name} detail={channel.kind}
                      removeLabel={`Remove channel ${channel.name}`}
                      onRemove={() => mutations.removeChannel.mutate(channel.id, removed)}
                    />
                  </ul>
                  {canRouteFrom(channel.kind) && (
                    <div className="mb-1.5 ml-3 border-l border-border pl-2">
                      <ul aria-label={`Bindings of ${channel.name}`} className="space-y-0.5">
                        {routes.map((binding) => (
                          <Row
                            key={binding.id} title={`to ${channelName(binding.targetChannelId)}`}
                            detail={binding.routingKey ? `routing key ${binding.routingKey}` : 'every message'}
                            removeLabel={`Remove binding from ${channel.name} to ${channelName(binding.targetChannelId)}`}
                            onRemove={() => mutations.removeBinding.mutate(binding.id, removed)}
                          />
                        ))}
                      </ul>
                      {bindingFrom === channel.id ? (
                        <form onSubmit={submitBinding(channel.id)} className="mt-1.5 space-y-2 pb-1">
                          <Field label="Route to">
                            <select name="targetChannelId" className={selectClass} required autoFocus>
                              {targets.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.kind})</option>)}
                            </select>
                          </Field>
                          <Field label="Routing key (optional)">
                            <Input name="routingKey" placeholder="order.created or order.*" maxLength={200} />
                          </Field>
                          <p className="text-[11px] leading-relaxed text-muted-foreground">
                            Without a key, every message is routed. With a key, a message is routed when its routing key is equal to it or matches it: * is one word, # is zero or more words.
                          </p>
                          <div className="flex gap-2">
                            <Button type="submit" size="sm" className="flex-1" disabled={mutations.addBinding.isPending || targets.length === 0}>Add binding</Button>
                            <Button type="button" variant="ghost" size="sm" onClick={() => { setBindingFrom(null); setError(null) }}>Cancel</Button>
                          </div>
                        </form>
                      ) : (
                        <Button
                          variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs text-muted-foreground"
                          disabled={targets.length === 0}
                          title={targets.length === 0 ? 'Add a queue or an exchange to route to.' : undefined}
                          onClick={() => { setError(null); setBindingFrom(channel.id) }}
                        >
                          <Plus className="h-3 w-3" />
                          {routes.length === 0 ? 'Bind to a channel' : 'Add binding'}
                        </Button>
                      )}
                    </div>
                  )}
                </li>
              )
            })}
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
              <div className="grid grid-cols-2 gap-2">
                <Field label="Attempts"><Input name="maxAttempts" type="number" min={1} max={10} defaultValue={1} required /></Field>
                <Field label="Backoff (ms)"><Input name="backoffMs" type="number" min={0} defaultValue={0} required /></Field>
              </div>
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

        <MessagesSection projectId={projectId} branchId={branchId} messages={messages} elements={elements} schemas={schemas} schemasLoading={schemasLoading} />
        <SchemasSection projectId={projectId} schemas={schemas} usedBy={schemaUsers} />
      </div>
    </div>
  )
}
