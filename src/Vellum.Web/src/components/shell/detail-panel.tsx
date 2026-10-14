import { createElement, useId, useState } from 'react'
import { useShellStore } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ArrowLeft, ArrowRight, MoreHorizontal, Plus, Trash2, X } from 'lucide-react'
import { DropdownMenu } from 'radix-ui'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { kindColor } from '@/lib/kind-colors'
import { ARCHITECTURE_ICONS, defaultIconForKind, getIconComponent } from '@/lib/default-icons'
import { CONTAINMENT_RULES } from '@/lib/containment-rules'
import { DiffDetail } from '@/components/review/diff-detail'
import { ConflictResolver } from '@/components/review/conflict-resolver'
import { CommentList } from '@/components/comments/comment-list'
import { CommentInput } from '@/components/comments/comment-input'
import { useComments } from '@/hooks/use-draft-comments'
import { ElementDocs } from '@/components/docs/element-docs'
import type { Provenance } from '@/lib/fact-status'
import { FactTruth } from './fact-truth'

interface ElementData {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  tags: string[]
  parentId: string | null
  icon: string | null
  factStatus?: string
  provenance?: Provenance | null
}

interface RelationshipData {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
  lineShape: string | null
  kind?: string
  factStatus?: string
  provenance?: Provenance | null
}

interface MessageData {
  id: string
  name: string
  description: string | null
  producerId: string
  consumerIds: string[]
  schemaId: string | null
  tags: string[]
  factStatus?: string
  provenance?: Provenance | null
}

interface SchemaData {
  id: string
  name: string
  description: string | null
  content: string
  version: number
}

interface DetailPanelProps {
  projectId: string
  elements: ElementData[]
  relationships: RelationshipData[]
  messages?: MessageData[]
  schemas?: SchemaData[]
  onUpdateElement?: (id: string, fields: Partial<ElementData>) => void
  onUpdateRelationship?: (id: string, fields: Partial<RelationshipData>) => void
  onDeleteElement?: (id: string) => void
  onDeleteRelationship?: (id: string) => void
  /** Opens the Add Element dialog for a child of this kind inside the element. */
  onAddChild?: (parentId: string, kind: string) => void
  /** The model cannot be changed: the fields show their values and take no input. */
  readOnly?: boolean
}

export function DetailPanel({
  projectId,
  elements,
  relationships,
  messages,
  schemas,
  onUpdateElement,
  onUpdateRelationship,
  onDeleteElement,
  onDeleteRelationship,
  onAddChild,
  readOnly = false,
}: DetailPanelProps) {
  const { detailPanelOpen, selectedElementId, selectedRelationshipId, selectedMessageId, selectElement, selectRelationship, selectMessage } =
    useShellStore()
  const { isReviewMode, reviewData, activeDraftId } = useDraftStore()
  // Each control has its label tied to it by ID, so a screen reader names the field.
  const fieldId = useId()
  // The tab stays when the selection moves, so following a connection keeps the Connections tab.
  const [elementTab, setElementTab] = useState<ElementTab>('details')
  const idFor = (field: string) => `${fieldId}-${field}`

  const { data: commentsData } = useComments(projectId, activeDraftId)
  const comments = commentsData?.items ?? []

  const selectedElement = elements.find((e) => e.id === selectedElementId)
  const selectedRel = relationships.find((r) => r.id === selectedRelationshipId)
  const selectedMessage = messages?.find((m) => m.id === selectedMessageId)

  // Look up review info for the selected entity
  const selectedChange = isReviewMode && reviewData
    ? reviewData.autoResolved.find(
        (c) => c.entityId === (selectedElementId ?? selectedRelationshipId)
      )
    : null
  const selectedConflict = isReviewMode && reviewData
    ? reviewData.conflicts.find(
        (c) => c.entityId === (selectedElementId ?? selectedRelationshipId)
      )
    : null

  const handleClose = () => {
    selectElement(null)
    selectRelationship(null)
    selectMessage(null)
  }

  const producer = selectedMessage ? elements.find((e) => e.id === selectedMessage.producerId) : null
  const consumers = selectedMessage
    ? elements.filter((e) => selectedMessage.consumerIds.includes(e.id))
    : []
  const attachedSchema = selectedMessage?.schemaId
    ? schemas?.find((s) => s.id === selectedMessage.schemaId)
    : null

  return (
    <Sheet open={detailPanelOpen} modal={false} onOpenChange={(open) => !open && handleClose()}>
      <SheetContent
        side="right"
        overlay={false}
        // The panel sits beside the canvas and does not trap focus: focus stays on the node
        // that opened it, so a field does not look active before anyone edits it.
        onOpenAutoFocus={(event) => event.preventDefault()}
        className="w-80 overflow-y-auto border-l border-border bg-card"
      >
        {readOnly && <p role="note" className="mx-4 mt-10 rounded-md border border-dashed border-border px-2.5 py-1.5 text-[11px] text-muted-foreground">Read-only. The fields show the values and take no change.</p>}
        <fieldset disabled={readOnly} className="m-0 min-w-0 border-0 p-0">
        {selectedMessage && (
          <div key={selectedMessage.id}>
            <SheetHeader>
              <SheetTitle className="flex items-center gap-2 font-display">
                <span className="text-muted-foreground">✉</span>
                {selectedMessage.name}
              </SheetTitle>
              <Badge variant="outline" className="w-fit font-mono text-xs">
                message
              </Badge>
            </SheetHeader>

            <div className="mt-4 space-y-4 px-4 pb-4">
              {selectedMessage.description && (
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">Description</p>
                  <p className="text-sm text-foreground">{selectedMessage.description}</p>
                </div>
              )}

              <div>
                <p className="mb-1 block text-xs text-muted-foreground">Producer</p>
                {producer ? (
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-auto px-0 py-0 text-sm font-normal text-primary hover:text-primary"
                    onClick={() => selectElement(producer.id)}
                  >
                    {producer.name}
                  </Button>
                ) : (
                  <p className="text-xs text-muted-foreground">Unknown</p>
                )}
              </div>

              {consumers.length > 0 && (
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">Consumers</p>
                  <div className="flex flex-col gap-1">
                    {consumers.map((c) => (
                      <Button
                        key={c.id}
                        variant="ghost"
                        size="sm"
                        className="h-auto justify-start px-0 py-0 text-sm font-normal text-primary hover:text-primary"
                        onClick={() => selectElement(c.id)}
                      >
                        {c.name}
                      </Button>
                    ))}
                  </div>
                </div>
              )}

              {attachedSchema && (
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">Schema</p>
                  <div className="rounded border border-border bg-background p-2">
                    <p className="font-mono text-xs font-medium text-foreground">
                      {attachedSchema.name}
                      <span className="ml-2 text-muted-foreground">v{attachedSchema.version}</span>
                    </p>
                    {attachedSchema.content && (
                      <pre className="mt-2 overflow-auto text-[10px] text-muted-foreground">
                        {(() => {
                          try {
                            return JSON.stringify(JSON.parse(attachedSchema.content), null, 2)
                          } catch {
                            return attachedSchema.content
                          }
                        })()}
                      </pre>
                    )}
                  </div>
                </div>
              )}

              {selectedMessage.tags.length > 0 && (
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">Tags</p>
                  <div className="flex flex-wrap gap-1">
                    {selectedMessage.tags.map((tag) => (
                      <Badge key={tag} variant="secondary" className="text-xs">
                        {tag}
                      </Badge>
                    ))}
                  </div>
                </div>
              )}

              <section aria-labelledby={idFor('message-truth')}>
                <h3 id={idFor('message-truth')} className={SECTION_HEADING}>Truth</h3>
                <FactTruth
                  projectId={projectId} entityType="message" entityId={selectedMessage.id}
                  factStatus={selectedMessage.factStatus} provenance={selectedMessage.provenance} readOnly={readOnly}
                />
              </section>
            </div>
          </div>
        )}

        {selectedElement && (
          <div key={selectedElement.id}>
            <SheetHeader className="flex-row items-center gap-3 pr-12">
              <ElementIconTile icon={selectedElement.icon} kind={selectedElement.kind} />
              <div className="min-w-0 flex-1">
                <SheetTitle className="truncate font-display text-base leading-tight">{selectedElement.name}</SheetTitle>
                <p className="mt-0.5 font-mono text-[10px] uppercase tracking-wider" style={{ color: kindColor(selectedElement.kind) }}>
                  {selectedElement.kind}
                </p>
              </div>
              {!isReviewMode && onDeleteElement && (
                <MoreActions
                  label={`More actions for ${selectedElement.name}`}
                  deleteLabel="Delete element"
                  onDelete={() => {
                    if (window.confirm(`Delete "${selectedElement.name}"?`)) {
                      onDeleteElement(selectedElement.id)
                      handleClose()
                    }
                  }}
                />
              )}
            </SheetHeader>

            {isReviewMode ? (
              <>
                {selectedConflict && <ConflictResolver conflict={selectedConflict} />}
                {selectedChange && !selectedConflict && <DiffDetail change={selectedChange} />}
                {!selectedConflict && !selectedChange && (
                  <p className="mt-4 text-xs text-muted-foreground">No changes to this element.</p>
                )}
                {activeDraftId && (
                  <div className="mt-4 space-y-2">
                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Comments</p>
                    <CommentList
                      projectId={projectId}
                      draftId={activeDraftId}
                      comments={comments}
                      entityId={selectedElement.id}
                    />
                    <CommentInput
                      projectId={projectId}
                      draftId={activeDraftId}
                      entityId={selectedElement.id}
                      entityType="element"
                    />
                  </div>
                )}
              </>
            ) : (
              <>
                <ElementTabs
                  idPrefix={fieldId}
                  tab={elementTab}
                  onTab={setElementTab}
                  connectionCount={relationships.filter((r) => r.fromId === selectedElement.id || r.toId === selectedElement.id).length}
                />
                {elementTab === 'connections' ? (
                  <div role="tabpanel" id={`${fieldId}-panel-connections`} aria-labelledby={`${fieldId}-tab-connections`} className="px-4 pb-4 pt-4">
                    <Connections
                      element={selectedElement}
                      elements={elements}
                      relationships={relationships}
                      onSelectElement={selectElement}
                      onSelectRelationship={selectRelationship}
                    />
                  </div>
                ) : (
              <div role="tabpanel" id={`${fieldId}-panel-details`} aria-labelledby={`${fieldId}-tab-details`} className="space-y-5 px-4 pb-4 pt-4">
                <Textarea
                  aria-label="Description"
                  placeholder="Add a description"
                  defaultValue={selectedElement.description ?? ''}
                  onBlur={(e) => onUpdateElement?.(selectedElement.id, { description: e.target.value || null })}
                  rows={2}
                  className={`${INLINE_FIELD} min-h-0 resize-none text-sm leading-relaxed`}
                />
                <dl className="grid grid-cols-[88px_1fr] items-center gap-x-3 gap-y-1.5 border-t border-border pt-4">
                  <dt><label htmlFor={idFor('element-name')} className="text-xs text-muted-foreground">Name</label></dt>
                  <dd>
                    <Input
                      id={idFor('element-name')}
                      defaultValue={selectedElement.name}
                      onBlur={(e) => onUpdateElement?.(selectedElement.id, { name: e.target.value })}
                      className={INLINE_FIELD}
                    />
                  </dd>
                  <dt><label htmlFor={idFor('element-technology')} className="text-xs text-muted-foreground">Technology</label></dt>
                  <dd>
                    <Input
                      id={idFor('element-technology')}
                      placeholder="None"
                      defaultValue={selectedElement.technology ?? ''}
                      onBlur={(e) => onUpdateElement?.(selectedElement.id, { technology: e.target.value || null })}
                      className={INLINE_FIELD}
                    />
                  </dd>
                  <dt><label htmlFor={idFor('element-status')} className="text-xs text-muted-foreground">Status</label></dt>
                  <dd>
                    <StatusSelect
                      id={idFor('element-status')}
                      value={selectedElement.status}
                      onChange={(status) => onUpdateElement?.(selectedElement.id, { status })}
                    />
                  </dd>
                  <dt><span id={idFor('element-icon')} className="text-xs text-muted-foreground">Icon</span></dt>
                  <dd>
                    <IconPicker
                      labelledBy={idFor('element-icon')}
                      current={selectedElement.icon ?? defaultIconForKind(selectedElement.kind)}
                      onPick={(icon) => onUpdateElement?.(selectedElement.id, { icon })}
                    />
                  </dd>
                </dl>
                <section aria-labelledby={idFor('element-truth')}>
                  <h3 id={idFor('element-truth')} className={SECTION_HEADING}>Truth</h3>
                  <FactTruth
                    projectId={projectId} entityType="element" entityId={selectedElement.id}
                    factStatus={selectedElement.factStatus} provenance={selectedElement.provenance} readOnly={readOnly}
                  />
                </section>
                <section aria-labelledby={idFor('element-tags')}>
                  <h3 id={idFor('element-tags')} className={SECTION_HEADING}>Tags</h3>
                  <TagEditor
                    tags={selectedElement.tags}
                    onChange={(tags) => onUpdateElement?.(selectedElement.id, { tags })}
                  />
                </section>
                {(CONTAINMENT_RULES[selectedElement.kind]?.length ?? 0) > 0 && (
                  <section aria-labelledby="detail-inside-label">
                    <h3 className={SECTION_HEADING} id="detail-inside-label">Inside this {selectedElement.kind}</h3>
                    {(() => {
                      const children = elements.filter((e) => e.parentId === selectedElement.id)
                      return children.length === 0 ? (
                        <p className="text-xs leading-relaxed text-muted-foreground">
                          Nothing yet. A {selectedElement.kind} holds {CONTAINMENT_RULES[selectedElement.kind].map((k) => `${k}s`).join(' and ')}.
                        </p>
                      ) : (
                        <ul aria-labelledby="detail-inside-label" className="divide-y divide-border overflow-hidden rounded-md border border-border">
                          {children.map((child) => (
                            <li key={child.id}>
                              <button
                                type="button"
                                className="flex w-full items-center gap-2 px-2.5 py-1.5 text-left text-sm text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary"
                                onClick={() => selectElement(child.id)}
                              >
                                <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: kindColor(child.kind) }} />
                                <span className="min-w-0 flex-1 truncate">{child.name}</span>
                                <span className="font-mono text-[10px] uppercase text-muted-foreground">{child.kind}</span>
                              </button>
                            </li>
                          ))}
                        </ul>
                      )
                    })()}
                    {onAddChild && (
                      <div className="mt-2 flex flex-wrap gap-1.5">
                        {CONTAINMENT_RULES[selectedElement.kind].map((kind) => (
                          <Button
                            key={kind}
                            variant="outline"
                            size="sm"
                            className="h-7 gap-1 px-2 text-xs"
                            onClick={() => onAddChild(selectedElement.id, kind)}
                          >
                            <Plus className="h-3 w-3" aria-hidden="true" />
                            Add {kind}
                          </Button>
                        ))}
                      </div>
                    )}
                  </section>
                )}
                <ElementDocs projectId={projectId} element={selectedElement} />
              </div>
                )}
              </>
            )}
          </div>
        )}

        {selectedRel && (
          <>
            <SheetHeader className="flex-row items-center gap-3 pr-12">
              <SheetTitle className="min-w-0 flex-1 font-display">Relationship</SheetTitle>
              {!isReviewMode && onDeleteRelationship && (
                <MoreActions
                  label="More actions for this relationship"
                  deleteLabel="Delete relationship"
                  onDelete={() => {
                    if (window.confirm('Delete this relationship?')) {
                      onDeleteRelationship(selectedRel.id)
                      handleClose()
                    }
                  }}
                />
              )}
            </SheetHeader>
            {isReviewMode ? (
              <>
                {selectedConflict && <ConflictResolver conflict={selectedConflict} />}
                {selectedChange && !selectedConflict && <DiffDetail change={selectedChange} />}
                {!selectedConflict && !selectedChange && (
                  <p className="mt-4 text-xs text-muted-foreground">No changes to this relationship.</p>
                )}
                {activeDraftId && (
                  <div className="mt-4 space-y-2">
                    <p className="text-xs font-medium text-muted-foreground uppercase tracking-wider">Comments</p>
                    <CommentList
                      projectId={projectId}
                      draftId={activeDraftId}
                      comments={comments}
                      entityId={selectedRel.id}
                    />
                    <CommentInput
                      projectId={projectId}
                      draftId={activeDraftId}
                      entityId={selectedRel.id}
                      entityType="relationship"
                    />
                  </div>
                )}
              </>
            ) : (
              <div className="mt-4 space-y-4 px-4 pb-4">
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">From</p>
                  <ElementLinkOrUnknown element={elements.find((e) => e.id === selectedRel.fromId)} onSelect={selectElement} />
                </div>
                <div>
                  <p className="mb-1 block text-xs text-muted-foreground">To</p>
                  <ElementLinkOrUnknown element={elements.find((e) => e.id === selectedRel.toId)} onSelect={selectElement} />
                </div>
                <div>
                  <label htmlFor={idFor('relationship-label')} className="mb-1 block text-xs text-muted-foreground">Label</label>
                  <Input
                    id={idFor('relationship-label')}
                    defaultValue={selectedRel.label ?? ''}
                    onBlur={(e) =>
                      onUpdateRelationship?.(selectedRel.id, { label: e.target.value || null })
                    }
                  />
                </div>
                <div>
                  <label htmlFor={idFor('relationship-technology')} className="mb-1 block text-xs text-muted-foreground">Technology</label>
                  <Input
                    id={idFor('relationship-technology')}
                    defaultValue={selectedRel.technology ?? ''}
                    onBlur={(e) =>
                      onUpdateRelationship?.(selectedRel.id, { technology: e.target.value || null })
                    }
                  />
                </div>
                <div>
                  <span id={idFor('relationship-kind')} className="mb-1 block text-xs text-muted-foreground">Kind</span>
                  <KindToggle
                    labelledBy={idFor('relationship-kind')}
                    value={selectedRel.kind ?? 'structural'}
                    onChange={(kind) => onUpdateRelationship?.(selectedRel.id, { kind })}
                  />
                </div>
                <div>
                  <label htmlFor={idFor('relationship-line')} className="mb-1 block text-xs text-muted-foreground">Line Style</label>
                  <select
                    id={idFor('relationship-line')}
                    defaultValue={selectedRel.lineShape ?? 'bezier'}
                    onChange={(e) => onUpdateRelationship?.(selectedRel.id, { lineShape: e.target.value })}
                    className="w-full rounded border border-input bg-background px-3 py-2 text-sm"
                  >
                    <option value="bezier">Curved (Bezier)</option>
                    <option value="straight">Straight</option>
                    <option value="step">Right-angle (Step)</option>
                  </select>
                </div>
                <section aria-labelledby={idFor('relationship-truth')}>
                  <h3 id={idFor('relationship-truth')} className={SECTION_HEADING}>Truth</h3>
                  <FactTruth
                    projectId={projectId} entityType="relationship" entityId={selectedRel.id}
                    factStatus={selectedRel.factStatus} provenance={selectedRel.provenance} readOnly={readOnly}
                  />
                </section>
              </div>
            )}
          </>
        )}
        </fieldset>
      </SheetContent>
    </Sheet>
  )
}

/** A field that reads as a value until it is hovered or edited. */
const INLINE_FIELD =
  'border-transparent bg-transparent px-1.5 shadow-none hover:border-input focus-visible:border-ring dark:bg-transparent'

const SECTION_HEADING = 'mb-2 text-[11px] font-medium uppercase tracking-wider text-muted-foreground'

const STATUS_COLOR: Record<string, string> = {
  current: 'var(--trace-request)',
  planned: 'var(--trace-retry)',
  deprecated: 'var(--trace-failure)',
}

function ElementIconTile({ icon, kind }: { icon: string | null; kind: string }) {
  const Icon = getIconComponent(icon ?? defaultIconForKind(kind))
  const color = kindColor(kind)
  return (
    <span
      aria-hidden="true"
      className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border"
      style={{ color, borderColor: `color-mix(in oklab, ${color} 35%, transparent)`, backgroundColor: `color-mix(in oklab, ${color} 12%, transparent)` }}
    >
      {Icon && createElement(Icon, { className: 'h-[18px] w-[18px]' })}
    </span>
  )
}

function StatusSelect({ id, value, onChange }: { id: string; value: string; onChange: (status: string) => void }) {
  return (
    <span className="relative ml-1.5 inline-flex items-center">
      <span aria-hidden="true" className="pointer-events-none absolute left-2.5 h-1.5 w-1.5 rounded-full" style={{ backgroundColor: STATUS_COLOR[value] ?? 'currentColor' }} />
      <select
        id={id}
        defaultValue={value}
        onChange={(e) => onChange(e.target.value)}
        className="h-7 appearance-none rounded-full border border-border bg-muted/40 pl-6 pr-3 text-xs text-foreground hover:border-input focus-visible:border-ring focus-visible:outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
      >
        <option value="current">Current</option>
        <option value="planned">Planned</option>
        <option value="deprecated">Deprecated</option>
      </select>
    </span>
  )
}

const KINDS = [
  { value: 'structural', label: 'Structural', hint: 'One part uses or depends on the other.' },
  { value: 'behavioural', label: 'Behavioural', hint: 'One part calls or sends to the other at run time.' },
] as const

/** Structural or behavioural. The two kinds are drawn differently and the review reads them differently. */
function KindToggle({ labelledBy, value, onChange }: { labelledBy: string; value: string; onChange: (kind: string) => void }) {
  return (
    <div role="radiogroup" aria-labelledby={labelledBy} className="inline-flex rounded-lg border border-border p-0.5">
      {KINDS.map((kind) => {
        const active = value === kind.value
        return (
          <button
            key={kind.value}
            type="button"
            role="radio"
            aria-checked={active}
            title={kind.hint}
            onClick={() => !active && onChange(kind.value)}
            className={`rounded-md px-2.5 py-1 text-xs focus-visible:outline-2 focus-visible:outline-primary ${
              active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:text-foreground'
            }`}
          >
            {kind.label}
          </button>
        )
      })}
    </div>
  )
}

function IconPicker({ labelledBy, current, onPick }: { labelledBy: string; current: string; onPick: (icon: string) => void }) {
  const [open, setOpen] = useState(false)
  const Current = getIconComponent(current)
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-labelledby={labelledBy}
          aria-describedby={`${labelledBy}-value`}
          className="inline-flex h-7 items-center gap-2 rounded-md border border-transparent px-1.5 text-sm text-foreground hover:border-input focus-visible:border-ring focus-visible:outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
        >
          {Current && createElement(Current, { className: 'h-4 w-4 text-muted-foreground', 'aria-hidden': true })}
          <span id={`${labelledBy}-value`}>{current}</span>
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-auto">
        <div role="group" aria-labelledby={labelledBy} className="grid grid-cols-6 gap-1">
          {ARCHITECTURE_ICONS.map((entry) => {
            const Icon = entry.icon
            const isActive = current === entry.name
            return (
              <button
                key={entry.name}
                className={`flex h-8 w-8 items-center justify-center rounded border text-muted-foreground hover:bg-accent ${
                  isActive ? 'border-primary bg-accent text-foreground' : 'border-transparent'
                }`}
                type="button"
                title={entry.name}
                aria-label={entry.name}
                aria-pressed={isActive}
                onClick={() => { onPick(entry.name); setOpen(false) }}
              >
                <Icon className="h-4 w-4" />
              </button>
            )
          })}
        </div>
      </PopoverContent>
    </Popover>
  )
}

function TagEditor({ tags, onChange }: { tags: string[]; onChange: (tags: string[]) => void }) {
  const add = (raw: string) => {
    const tag = raw.trim()
    if (tag && !tags.includes(tag)) onChange([...tags, tag])
  }
  const [draft, setDraft] = useState('')
  const submit = () => {
    add(draft)
    setDraft('')
  }
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {tags.map((tag) => (
        <span key={tag} className="inline-flex h-6 items-center gap-1 rounded-md border border-border bg-muted/50 pl-2 pr-1 text-xs text-foreground">
          {tag}
          <button
            type="button"
            aria-label={`Remove tag ${tag}`}
            className="rounded p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:outline-primary"
            onClick={() => onChange(tags.filter((t) => t !== tag))}
          >
            <X className="h-3 w-3" aria-hidden="true" />
          </button>
        </span>
      ))}
      {/* The input and its Add button wrap as one unit, so the button stays visible on a narrow panel. */}
      <div className="flex min-w-36 flex-1 items-center gap-1">
        <input
          aria-label="New tag"
          placeholder={tags.length === 0 ? 'Add a tag…' : 'New tag'}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          enterKeyHint="done"
          className="h-6 min-w-0 flex-1 rounded-md border border-dashed border-border bg-transparent px-2 text-xs text-foreground placeholder:text-muted-foreground hover:border-input focus-visible:border-ring focus-visible:outline-none"
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              submit()
            }
          }}
        />
        <button
          type="button"
          onClick={submit}
          disabled={draft.trim() === ''}
          className="inline-flex h-6 shrink-0 items-center gap-1 rounded-md border border-border px-1.5 text-xs text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary disabled:text-muted-foreground disabled:opacity-60 disabled:hover:bg-transparent"
        >
          <Plus className="h-3 w-3" aria-hidden="true" />
          Add tag
        </button>
      </div>
    </div>
  )
}

type ElementTab = 'details' | 'connections'

function ElementTabs({ idPrefix, tab, onTab, connectionCount }: {
  idPrefix: string
  tab: ElementTab
  onTab: (tab: ElementTab) => void
  connectionCount: number
}) {
  const tabs: { id: ElementTab; label: string; count?: number }[] = [
    { id: 'details', label: 'Details' },
    { id: 'connections', label: 'Connections', count: connectionCount },
  ]
  return (
    <div
      role="tablist"
      aria-label="Element sections"
      className="flex gap-4 border-b border-border px-4"
      onKeyDown={(e) => {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return
        e.preventDefault()
        const next = tab === 'details' ? 'connections' : 'details'
        onTab(next)
        document.getElementById(`${idPrefix}-tab-${next}`)?.focus()
      }}
    >
      {tabs.map((t) => {
        const active = tab === t.id
        return (
          <button
            key={t.id}
            type="button"
            role="tab"
            id={`${idPrefix}-tab-${t.id}`}
            aria-selected={active}
            aria-controls={`${idPrefix}-panel-${t.id}`}
            tabIndex={active ? 0 : -1}
            onClick={() => onTab(t.id)}
            className={`-mb-px inline-flex h-9 items-center gap-1.5 border-b-2 text-xs font-medium transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary ${
              active ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground'
            }`}
          >
            {t.label}
            {t.count !== undefined && (
              <span className="rounded-full bg-muted px-1.5 font-mono text-[10px] tabular-nums text-muted-foreground">{t.count}</span>
            )}
          </button>
        )
      })}
    </div>
  )
}

/** The relationships that leave and reach the element, each naming the element at the other end. */
function Connections({ element, elements, relationships, onSelectElement, onSelectRelationship }: {
  element: ElementData
  elements: ElementData[]
  relationships: RelationshipData[]
  onSelectElement: (id: string) => void
  onSelectRelationship: (id: string) => void
}) {
  const outgoing = relationships.filter((r) => r.fromId === element.id)
  const incoming = relationships.filter((r) => r.toId === element.id)
  if (outgoing.length === 0 && incoming.length === 0) {
    return (
      <p className="text-xs leading-relaxed text-muted-foreground">
        No relationships yet. Drag from a handle of this {element.kind} on the canvas to connect it.
      </p>
    )
  }
  const group = (title: string, direction: 'out' | 'in', items: RelationshipData[]) => {
    const headingId = `connections-${direction}`
    return (
      <section aria-labelledby={headingId}>
        <h3 id={headingId} className={SECTION_HEADING}>{title} <span className="font-mono tabular-nums">{items.length}</span></h3>
        {items.length === 0 ? (
          <p className="text-xs text-muted-foreground">None.</p>
        ) : (
          <ul aria-labelledby={headingId} className="divide-y divide-border overflow-hidden rounded-md border border-border">
            {items.map((rel) => {
              const otherId = direction === 'out' ? rel.toId : rel.fromId
              const other = elements.find((e) => e.id === otherId)
              const Arrow = direction === 'out' ? ArrowRight : ArrowLeft
              return (
                <li key={rel.id} className="flex items-stretch">
                  <button
                    type="button"
                    disabled={!other}
                    onClick={() => other && onSelectElement(other.id)}
                    className="flex min-w-0 flex-1 items-start gap-2 px-2.5 py-1.5 text-left hover:bg-muted focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary disabled:cursor-default disabled:hover:bg-transparent"
                  >
                    <Arrow aria-hidden="true" className="mt-1 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-2">
                        {other && <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: kindColor(other.kind) }} />}
                        <span className="min-w-0 truncate text-sm text-foreground">{other?.name ?? 'Removed element'}</span>
                      </span>
                      {(rel.label || rel.technology) && (
                        <span className={`block truncate text-xs text-muted-foreground ${other ? 'pl-4' : ''}`}>
                          {rel.label}
                          {rel.label && rel.technology && ' · '}
                          {rel.technology && <span className="font-mono text-[11px]">{rel.technology}</span>}
                        </span>
                      )}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => onSelectRelationship(rel.id)}
                    title="Open this relationship"
                    aria-label={`Open the relationship ${direction === 'out' ? 'to' : 'from'} ${other?.name ?? 'a removed element'}`}
                    className="shrink-0 border-l border-border px-2.5 text-[11px] text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary"
                  >
                    Edit
                  </button>
                </li>
              )
            })}
          </ul>
        )}
      </section>
    )
  }
  return (
    <div className="space-y-5">
      {group('Outgoing', 'out', outgoing)}
      {group('Incoming', 'in', incoming)}
    </div>
  )
}

function ElementLinkOrUnknown({ element, onSelect }: { element: ElementData | undefined; onSelect: (id: string) => void }) {
  if (!element) return <p className="text-sm text-muted-foreground">Unknown</p>
  return (
    <button
      type="button"
      onClick={() => onSelect(element.id)}
      className="inline-flex items-center gap-2 rounded text-sm text-foreground underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary"
    >
      <span aria-hidden="true" className="h-2 w-2 shrink-0 rounded-full" style={{ backgroundColor: kindColor(element.kind) }} />
      {element.name}
    </button>
  )
}

/** The header overflow menu. Delete lives here, away from the fields, and still asks first. */
function MoreActions({ label, deleteLabel, onDelete }: { label: string; deleteLabel: string; onDelete: () => void }) {
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <Button variant="ghost" size="icon-sm" aria-label={label} title="More actions" className="shrink-0">
          <MoreHorizontal aria-hidden="true" />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={4}
          className="z-50 min-w-44 rounded-lg bg-popover p-1 text-sm text-popover-foreground shadow-md ring-1 ring-foreground/10"
        >
          <DropdownMenu.Item
            onSelect={onDelete}
            className="flex cursor-default items-center gap-2 rounded-md px-2 py-1.5 text-xs text-destructive outline-none data-[highlighted]:bg-destructive/10"
          >
            <Trash2 aria-hidden="true" className="h-3.5 w-3.5" />
            {deleteLabel}…
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}
