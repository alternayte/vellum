import { useMemo, useState } from 'react'
import { Copy } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { useBindings, useChannels, useSubscriptions } from '@/hooks/use-contracts'
import { useDeleteScenario, useSaveScenario, type ScenarioDocument } from '@/hooks/use-scenarios'
import { reachableChannels } from '@/lib/trace'

export interface ScenarioReference {
  group: 'Elements' | 'Operations' | 'Channels' | 'Subscriptions' | 'Messages'
  id: string
  label: string
}

interface ScenarioEditorProps {
  projectId: string
  branchId?: string | null
  scenario: ScenarioDocument
  isNew: boolean
  references: ScenarioReference[]
  onClose: () => void
  onPlay: (scenarioId: string) => void
}

const STEP_HELP = `Step types
  call     { "type":"call", "id", "from": element, "operation": operation, "request", "response", "then": [], "onError": [] }
  publish  { "type":"publish", "id", "from": element, "channel", "message", "payload", "headers": {}, "handlers": { subscription: [] },
             "routingKey": "order.created" (optional) }
           A handler names a subscription on the channel or on a channel that its bindings route to.
           A binding with a routing key is followed when the step's key is equal to it or matches it (* one word, # zero or more).
  write    { "type":"write", "id", "by": element, "store": element, "dataset": "orders", "change" }
  parallel { "type":"parallel", "id", "branches": [[], []] }
  branch   { "type":"branch", "id", "whenStep": step id, "is": "Succeeded" | "Failed" | "Skipped", "then": [], "else": [] }
Faults      { "target": id, "kind": "Error" | "Timeout" | "Outage", "failFirst": 1 | null }
Assertions  { "type":"outcome", "id", "step", "expected": "Succeeded" }
            { "type":"event-count", "id", "kind": "DeadLettered", "target": id | null, "expected": 1 }`

/**
 * Edits a scenario as its stored document. Contract IDs are copied from the reference list;
 * the server checks every reference and payload when the scenario plays.
 */
export function ScenarioEditor({ projectId, branchId, scenario, isNew, references, onClose, onPlay }: ScenarioEditorProps) {
  const { data: channels = [] } = useChannels(projectId, branchId)
  const { data: subscriptions = [] } = useSubscriptions(projectId, branchId)
  const { data: bindings = [] } = useBindings(projectId, branchId)
  const save = useSaveScenario(projectId)
  const remove = useDeleteScenario(projectId)
  const [name, setName] = useState(scenario.name)
  const [text, setText] = useState(() =>
    JSON.stringify({ steps: scenario.steps, faults: scenario.faults, assertions: scenario.assertions }, null, 2))
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

  // Subscriptions are listed by the channel a step publishes to, so they are not a group of their own.
  const groups = useMemo(() => {
    const byGroup = new Map<string, ScenarioReference[]>()
    for (const reference of references) {
      if (reference.group !== 'Subscriptions') byGroup.set(reference.group, [...(byGroup.get(reference.group) ?? []), reference])
    }
    return [...byGroup.entries()]
  }, [references])

  // For each channel a step can publish to: the subscriptions a handler may name, by the
  // channel they listen to. The published channel comes first, then the channels its bindings reach.
  const handlerChoices = useMemo(() => {
    const labelOf = new Map(references.map((r) => [r.id, r.label]))
    const channelName = new Map(channels.map((c) => [c.id, c.name]))
    return channels
      .map((channel) => ({
        channel,
        reached: reachableChannels(channel.id, bindings)
          .map((id) => ({
            id,
            name: channelName.get(id) ?? 'Unknown channel',
            subscriptions: subscriptions.filter((s) => s.channelId === id).map((s) => ({ id: s.id, label: labelOf.get(s.id) ?? s.id })),
          }))
          .filter((group) => group.subscriptions.length > 0),
      }))
      .filter((choice) => choice.reached.length > 0)
  }, [references, channels, subscriptions, bindings])

  const submit = (thenPlay: boolean) => {
    let body: { steps?: unknown; faults?: unknown; assertions?: unknown }
    try {
      body = JSON.parse(text)
    } catch (e) {
      setError(`The document is not valid JSON. ${e instanceof Error ? e.message : ''}`)
      return
    }
    if (!name.trim()) {
      setError('Give the scenario a name.')
      return
    }
    setError(null)
    save.mutate(
      {
        isNew,
        scenario: {
          id: scenario.id,
          name: name.trim(),
          steps: Array.isArray(body.steps) ? body.steps : [],
          faults: Array.isArray(body.faults) ? body.faults : [],
          assertions: Array.isArray(body.assertions) ? body.assertions : [],
        },
      },
      {
        onSuccess: () => (thenPlay ? onPlay(scenario.id) : onClose()),
        onError: (e) => setError(e.message),
      },
    )
  }

  const copy = async (id: string) => {
    await navigator.clipboard.writeText(id)
    setCopied(id)
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="flex max-h-[90vh] flex-col gap-3 sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>{isNew ? 'New scenario' : 'Edit scenario'}</DialogTitle>
          <DialogDescription>
            Steps reference contracts by ID. Copy an ID from the list; references and payloads are checked when the scenario plays.
          </DialogDescription>
        </DialogHeader>

        <label className="block text-xs font-medium text-muted-foreground">
          Name
          <Input className="mt-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="Place an order" autoFocus />
        </label>

        <div className="grid min-h-0 flex-1 gap-3 md:grid-cols-[minmax(0,1fr)_15rem]">
          <div className="flex min-h-0 flex-col gap-2">
            <label className="flex min-h-0 flex-1 flex-col text-xs font-medium text-muted-foreground">
              Document
              <textarea
                value={text}
                onChange={(e) => setText(e.target.value)}
                spellCheck={false}
                className="mt-1 min-h-64 flex-1 resize-none rounded-lg border border-input bg-background p-3 font-mono text-xs leading-relaxed text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
              />
            </label>
            <details className="text-xs text-muted-foreground">
              <summary className="cursor-pointer rounded hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary">Format reference</summary>
              <pre className="mt-1.5 overflow-x-auto rounded-md border border-border bg-background p-2 font-mono text-[10px] leading-relaxed">{STEP_HELP}</pre>
            </details>
          </div>

          <div className="min-h-0 overflow-y-auto rounded-lg border border-border p-2 md:max-h-[26rem]">
            {groups.length === 0 && handlerChoices.length === 0 && <p className="text-xs text-muted-foreground">The model has nothing to reference yet. Add elements and contracts first.</p>}
            {groups.map(([group, items]) => (
              <div key={group} className="mb-2 last:mb-0">
                <p className="px-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">{group}</p>
                <ul>
                  {items.map((item) => (
                    <li key={item.id}>
                      <button
                        type="button"
                        onClick={() => copy(item.id)}
                        title={item.id}
                        className="flex w-full items-center gap-1.5 rounded px-1 py-1 text-left text-xs text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                      >
                        <span className="min-w-0 flex-1 truncate">{item.label}</span>
                        <span className="shrink-0 font-mono text-[10px] text-muted-foreground" aria-live="polite">
                          {copied === item.id ? 'copied' : <Copy className="h-3 w-3" aria-label="Copy ID" />}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            ))}
            {handlerChoices.length > 0 && (
              <div className="mb-2 last:mb-0">
                <p className="px-1 text-[10px] font-medium uppercase tracking-wider text-muted-foreground">Handlers</p>
                {handlerChoices.map(({ channel, reached }) => (
                  <div key={channel.id} className="mt-1">
                    <p className="px-1 text-[11px] text-muted-foreground">A publish to <span className="text-foreground">{channel.name}</span> reaches</p>
                    {reached.map((group) => (
                      <div key={group.id} className="ml-1 border-l border-border pl-1.5">
                        {group.id !== channel.id && <p className="px-1 pt-0.5 font-mono text-[10px] text-muted-foreground">through {group.name}</p>}
                        <ul>
                          {group.subscriptions.map((item) => (
                            <li key={item.id}>
                              <button
                                type="button"
                                onClick={() => copy(item.id)}
                                title={item.id}
                                className="flex w-full items-center gap-1.5 rounded px-1 py-1 text-left text-xs text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary"
                              >
                                <span className="min-w-0 flex-1 truncate">{item.label}</span>
                                <span className="shrink-0 font-mono text-[10px] text-muted-foreground" aria-live="polite">
                                  {copied === item.id ? 'copied' : <Copy className="h-3 w-3" aria-label="Copy ID" />}
                                </span>
                              </button>
                            </li>
                          ))}
                        </ul>
                      </div>
                    ))}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        {error && <p role="alert" className="text-xs text-[var(--trace-failure)]">{error}</p>}

        <div className="flex flex-wrap items-center gap-2">
          {!isNew && (
            <Button
              variant="ghost" size="sm" className="text-[var(--trace-failure)]"
              disabled={remove.isPending}
              onClick={() => remove.mutate(scenario.id, { onSuccess: onClose, onError: (e) => setError(e.message) })}
            >
              Delete
            </Button>
          )}
          <div className="flex-1" />
          <Button variant="ghost" size="sm" onClick={onClose}>Cancel</Button>
          <Button variant="secondary" size="sm" disabled={save.isPending} onClick={() => submit(false)}>Save</Button>
          <Button size="sm" disabled={save.isPending} onClick={() => submit(true)}>Save and play</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
