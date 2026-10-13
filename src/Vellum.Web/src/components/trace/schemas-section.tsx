import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { useSchemaMutations, type Schema } from '@/hooks/use-schemas'
import { NO_FAILURE, formFailure, type FormFailure } from '@/hooks/use-contracts'
import { Field, FormError, Row, Section } from './contract-parts'

const NEW_SCHEMA = `{
  "type": "object",
  "properties": {
    "id": { "type": "string" }
  },
  "required": ["id"]
}`

const SHOWN_FIELDS = ['name', 'description', 'content']

interface SchemaDialogProps {
  projectId: string
  /** The schema to edit, or null for a new one. */
  schema: Schema | null
  onClose: () => void
}

/** The server checks the document when it saves; its reason shows under the field it names. */
function SchemaDialog({ projectId, schema, onClose }: SchemaDialogProps) {
  const { addSchema, updateSchema } = useSchemaMutations(projectId)
  const [content, setContent] = useState(schema?.content ?? NEW_SCHEMA)
  const [failure, setFailure] = useState<FormFailure>(NO_FAILURE)
  const pending = addSchema.isPending || updateSchema.isPending

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const fields = {
      name: String(form.get('name')).trim(),
      description: String(form.get('description')).trim() || null,
      content,
    }
    const done = { onSuccess: onClose, onError: (error: Error) => setFailure(formFailure(error, SHOWN_FIELDS)) }
    if (schema) updateSchema.mutate({ id: schema.id, ...fields }, done)
    else addSchema.mutate(fields, done)
  }

  const format = () => {
    try {
      setContent(JSON.stringify(JSON.parse(content), null, 2))
      setFailure((current) => ({ ...current, fields: { ...current.fields, content: '' } }))
    } catch (e) {
      setFailure((current) => ({
        ...current,
        fields: { ...current.fields, content: `Content is not valid JSON, so it cannot be formatted. ${e instanceof Error ? e.message : ''}`.trim() },
      }))
    }
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{schema ? 'Edit schema' : 'New schema'}</DialogTitle>
          <DialogDescription>
            {schema
              ? `Version ${schema.version}. A change of the content makes version ${schema.version + 1}. Every operation and message that uses the schema reads the new version.`
              : 'A schema is a JSON Schema document. Operations use it for a request or a response, and messages for their payload.'}
          </DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col gap-3" noValidate>
          <Field label="Name" error={failure.fields.name}>
            {(invalid) => <Input name="name" defaultValue={schema?.name ?? ''} placeholder="OrderPlaced" data-autofocus {...invalid} />}
          </Field>
          <Field label="Description (optional)" error={failure.fields.description}>
            {(invalid) => <Input name="description" defaultValue={schema?.description ?? ''} placeholder="What the document describes" {...invalid} />}
          </Field>
          <div className="flex min-h-0 flex-1 flex-col">
            <Field label="JSON Schema" error={failure.fields.content || undefined}>
              {(invalid) => (
                <textarea
                  name="content"
                  value={content}
                  onChange={(e) => setContent(e.target.value)}
                  spellCheck={false}
                  autoCapitalize="off"
                  autoCorrect="off"
                  wrap="off"
                  className="block h-72 max-h-[45vh] w-full resize-y rounded-lg border border-input bg-background p-3 font-mono text-xs leading-relaxed text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive"
                  {...invalid}
                />
              )}
            </Field>
          </div>
          <FormError message={failure.message} />
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={format}>Format</Button>
            <div className="flex-1" />
            <Button type="button" variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
            <Button type="submit" size="sm" disabled={pending}>{schema ? 'Save schema' : 'Add schema'}</Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  )
}

interface SchemasSectionProps {
  projectId: string
  schemas: Schema[]
  /** The names of the operations and messages of this branch that use each schema, by schema ID. */
  usedBy: Map<string, string[]>
}

export function SchemasSection({ projectId, schemas, usedBy }: SchemasSectionProps) {
  const { removeSchema } = useSchemaMutations(projectId)
  // The schema in the dialog: null for a new one, undefined when the dialog is closed.
  const [editing, setEditing] = useState<Schema | null | undefined>(undefined)
  const [refusal, setRefusal] = useState<{ id: string; message: string } | null>(null)

  const detail = (schema: Schema) => {
    const users = usedBy.get(schema.id) ?? []
    return `v${schema.version} · ${users.length === 0 ? 'not used' : `used by ${users.join(', ')}`}`
  }

  return (
    <Section
      title="Schemas" count={schemas.length} onToggle={() => setEditing(null)}
      hint="A schema is a JSON Schema document for a request, a response or a message payload. Operations and messages choose one."
    >
      <ul className="mt-1.5 space-y-0.5">
        {schemas.map((schema) => (
          <Row
            key={schema.id} title={schema.name} detail={detail(schema)}
            editLabel={`Edit schema ${schema.name}`} onEdit={() => setEditing(schema)}
            removeLabel={`Remove schema ${schema.name}`}
            onRemove={() => {
              setRefusal(null)
              removeSchema.mutate(schema.id, { onError: (error) => setRefusal({ id: schema.id, message: error.message }) })
            }}
            error={refusal?.id === schema.id ? refusal.message : null}
          />
        ))}
      </ul>
      {editing !== undefined && (
        <SchemaDialog key={editing?.id ?? 'new'} projectId={projectId} schema={editing} onClose={() => setEditing(undefined)} />
      )}
    </Section>
  )
}
