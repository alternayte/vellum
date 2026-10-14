import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { useMessageMutations, type Message, type MessageFields } from '@/hooks/use-messages'
import type { Schema } from '@/hooks/use-schemas'
import { NO_FAILURE, formFailure, type FormFailure } from '@/hooks/use-contracts'
import { Field, FormError, Row, SchemaSelect, Section, selectClass, type NamedElement } from './contract-parts'

const SHOWN_FIELDS = ['name', 'description', 'producerId', 'consumerIds', 'schemaId', 'tags']

interface MessageFormProps {
  message?: Message
  elements: NamedElement[]
  schemas: Schema[]
  /** True while the schema list has not loaded. */
  schemasLoading: boolean
  failure: FormFailure
  pending: boolean
  onSubmit: (fields: MessageFields) => void
  onCancel?: () => void
}

function MessageForm({ message, elements, schemas, schemasLoading, failure, pending, onSubmit, onCancel }: MessageFormProps) {
  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    onSubmit({
      name: String(form.get('name')).trim(),
      description: String(form.get('description')).trim() || null,
      producerId: String(form.get('producerId')),
      consumerIds: form.getAll('consumerIds').map(String),
      schemaId: String(form.get('schemaId')) || null,
      tags: String(form.get('tags')).split(',').map((tag) => tag.trim()).filter(Boolean),
    })
  }

  return (
    <form onSubmit={submit} className="mt-2 space-y-2" noValidate>
      <Field label="Name" error={failure.fields.name}>
        {(invalid) => <Input name="name" defaultValue={message?.name ?? ''} placeholder="OrderPlaced" autoFocus {...invalid} />}
      </Field>
      <Field label="Description (optional)" error={failure.fields.description}>
        {(invalid) => <Input name="description" defaultValue={message?.description ?? ''} placeholder="What the message says happened" {...invalid} />}
      </Field>
      <Field label="Producer" error={failure.fields.producerId}>
        {(invalid) => (
          <select name="producerId" className={selectClass} defaultValue={message?.producerId} {...invalid}>
            {elements.map((el) => <option key={el.id} value={el.id}>{el.name}</option>)}
          </select>
        )}
      </Field>
      <Field label="Schema" error={failure.fields.schemaId}>
        {(invalid) => (
          <SchemaSelect name="schemaId" initial={message?.schemaId} schemas={schemas} loading={schemasLoading} {...invalid} />
        )}
      </Field>
      {!schemasLoading && schemas.length === 0 && (
        <p className="text-[11px] leading-relaxed text-muted-foreground">No schema exists yet. Add one under Schemas, then choose it here.</p>
      )}
      <fieldset>
        <legend className="text-[11px] font-medium text-muted-foreground">Consumers (optional)</legend>
        <div className="mt-1 max-h-28 space-y-0.5 overflow-y-auto rounded-lg border border-input p-1.5">
          {elements.map((el) => (
            <label key={el.id} className="flex items-center gap-2 rounded px-1 py-0.5 text-xs text-foreground hover:bg-muted">
              <input type="checkbox" name="consumerIds" value={el.id} defaultChecked={message?.consumerIds.includes(el.id)} className="accent-[var(--primary)] [color-scheme:dark]" />
              <span className="min-w-0 truncate">{el.name}</span>
            </label>
          ))}
        </div>
        {failure.fields.consumerIds && <p role="alert" className="mt-1 text-[11px] leading-snug text-[var(--trace-failure)]">{failure.fields.consumerIds}</p>}
      </fieldset>
      <Field label="Tags (optional, with commas)" error={failure.fields.tags}>
        {(invalid) => <Input name="tags" defaultValue={message?.tags.join(', ') ?? ''} placeholder="orders, public" {...invalid} />}
      </Field>
      <FormError message={failure.message} />
      <div className="flex gap-2">
        <Button type="submit" size="sm" className="flex-1" disabled={pending || elements.length === 0}>{message ? 'Save message' : 'Add message'}</Button>
        {onCancel && <Button type="button" variant="ghost" size="sm" onClick={onCancel}>Cancel</Button>}
      </div>
    </form>
  )
}

interface MessagesSectionProps {
  projectId: string
  branchId?: string | null
  messages: Message[]
  elements: NamedElement[]
  schemas: Schema[]
  schemasLoading: boolean
  /** The messages of a revision: shown, not changed. */
  readOnly?: boolean
}

/** The form that is open: a new message, or the message with this ID. */
type Open = { kind: 'new' } | { kind: 'edit'; id: string } | null

export function MessagesSection({ projectId, branchId, messages, elements, schemas, schemasLoading, readOnly = false }: MessagesSectionProps) {
  const { addMessage, updateMessage, removeMessage } = useMessageMutations(projectId, branchId)
  const [open, setOpen] = useState<Open>(null)
  const [failure, setFailure] = useState<FormFailure>(NO_FAILURE)
  const [refusal, setRefusal] = useState<{ id: string; message: string } | null>(null)

  const show = (next: Open) => { setFailure(NO_FAILURE); setRefusal(null); setOpen(next) }
  const done = { onSuccess: () => show(null), onError: (error: Error) => setFailure(formFailure(error, SHOWN_FIELDS)) }
  const nameOf = (id: string) => elements.find((el) => el.id === id)?.name ?? 'Unknown element'
  const detail = (message: Message) => {
    const schema = schemas.find((s) => s.id === message.schemaId)
    const consumers = message.consumerIds.length === 0 ? '' : ` → ${message.consumerIds.map(nameOf).join(', ')}`
    const contract = schema ? `${schema.name} v${schema.version}` : message.schemaId ? 'schema not found' : 'no schema'
    return `${nameOf(message.producerId)}${consumers} · ${contract}`
  }

  return (
    <Section
      title="Messages" count={messages.length} adding={open?.kind === 'new'} onToggle={readOnly ? undefined : () => show(open?.kind === 'new' ? null : { kind: 'new' })}
      hint={elements.length === 0
        ? 'Add an element first. A message has a producer.'
        : 'A message is what a producer publishes to a channel, such as OrderPlaced. Its schema is the contract for the payload.'}
    >
      {open?.kind === 'new' && (
        <MessageForm
          elements={elements} schemas={schemas} schemasLoading={schemasLoading} failure={failure} pending={addMessage.isPending}
          onSubmit={(fields) => addMessage.mutate(fields, done)}
        />
      )}
      <ul className="mt-1.5 space-y-0.5">
        {messages.map((message) => open?.kind === 'edit' && open.id === message.id ? (
          <li key={message.id} className="rounded-md border border-border px-2 pb-2">
            <MessageForm
              message={message} elements={elements} schemas={schemas} schemasLoading={schemasLoading} failure={failure} pending={updateMessage.isPending}
              onSubmit={(fields) => updateMessage.mutate({ id: message.id, ...fields }, done)}
              onCancel={() => show(null)}
            />
          </li>
        ) : (
          <Row
            key={message.id} title={message.name} detail={detail(message)}
            editLabel={`Edit message ${message.name}`} onEdit={readOnly ? undefined : () => show({ kind: 'edit', id: message.id })}
            removeLabel={`Remove message ${message.name}`}
            onRemove={readOnly ? undefined : () => {
              setRefusal(null)
              removeMessage.mutate(message.id, { onError: (error) => setRefusal({ id: message.id, message: error.message }) })
            }}
            error={refusal?.id === message.id ? refusal.message : null}
          />
        ))}
      </ul>
    </Section>
  )
}
