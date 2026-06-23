import { useShellStore } from '@/stores/shell-store'
import { useDraftStore } from '@/stores/draft-store'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { kindColor } from '@/lib/kind-colors'
import { DiffDetail } from '@/components/review/diff-detail'
import { ConflictResolver } from '@/components/review/conflict-resolver'
import { CommentList } from '@/components/comments/comment-list'
import { CommentInput } from '@/components/comments/comment-input'
import { useComments } from '@/hooks/use-draft-comments'

interface ElementData {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  tags: string[]
  parentId: string | null
}

interface RelationshipData {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

interface MessageData {
  id: string
  name: string
  description: string | null
  producerId: string
  consumerIds: string[]
  schemaId: string | null
  tags: string[]
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
}

export function DetailPanel({
  projectId,
  elements,
  relationships,
  messages,
  schemas,
  onUpdateElement,
  onUpdateRelationship,
}: DetailPanelProps) {
  const { detailPanelOpen, selectedElementId, selectedRelationshipId, selectedMessageId, selectElement, selectRelationship, selectMessage } =
    useShellStore()
  const { isReviewMode, reviewData, activeDraftId } = useDraftStore()

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
    <Sheet open={detailPanelOpen} onOpenChange={(open) => !open && handleClose()}>
      <SheetContent side="right" className="w-80 border-l border-border bg-card">
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

            <div className="mt-4 space-y-4">
              {selectedMessage.description && (
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Description</label>
                  <p className="text-sm text-foreground">{selectedMessage.description}</p>
                </div>
              )}

              <div>
                <label className="mb-1 block text-xs text-muted-foreground">Producer</label>
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
                  <label className="mb-1 block text-xs text-muted-foreground">Consumers</label>
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
                  <label className="mb-1 block text-xs text-muted-foreground">Schema</label>
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
                  <label className="mb-1 block text-xs text-muted-foreground">Tags</label>
                  <div className="flex flex-wrap gap-1">
                    {selectedMessage.tags.map((tag) => (
                      <Badge key={tag} variant="secondary" className="text-xs">
                        {tag}
                      </Badge>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>
        )}

        {selectedElement && (
          <div key={selectedElement.id}>
            <SheetHeader>
              <SheetTitle className="flex items-center gap-2 font-display">
                <span
                  className="h-3 w-1 rounded-full"
                  style={{ backgroundColor: kindColor(selectedElement.kind) }}
                />
                {selectedElement.name}
              </SheetTitle>
              <Badge variant="outline" className="w-fit font-mono text-xs">
                {selectedElement.kind}
              </Badge>
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
              <div className="mt-4 space-y-4">
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Name</label>
                  <Input
                    defaultValue={selectedElement.name}
                    onBlur={(e) => onUpdateElement?.(selectedElement.id, { name: e.target.value })}
                  />
                </div>
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Description</label>
                  <Textarea
                    defaultValue={selectedElement.description ?? ''}
                    onBlur={(e) =>
                      onUpdateElement?.(selectedElement.id, { description: e.target.value || null })
                    }
                    rows={3}
                  />
                </div>
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Technology</label>
                  <Input
                    defaultValue={selectedElement.technology ?? ''}
                    onBlur={(e) =>
                      onUpdateElement?.(selectedElement.id, { technology: e.target.value || null })
                    }
                  />
                </div>
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Status</label>
                  <select
                    defaultValue={selectedElement.status}
                    onChange={(e) => onUpdateElement?.(selectedElement.id, { status: e.target.value })}
                    className="w-full rounded border border-input bg-background px-3 py-2 text-sm"
                  >
                    <option value="current">Current</option>
                    <option value="planned">Planned</option>
                    <option value="deprecated">Deprecated</option>
                  </select>
                </div>
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Tags</label>
                  <div className="flex flex-wrap gap-1">
                    {selectedElement.tags.map((tag) => (
                      <Badge key={tag} variant="secondary" className="text-xs">
                        {tag}
                      </Badge>
                    ))}
                  </div>
                </div>
              </div>
            )}
          </div>
        )}

        {selectedRel && (
          <>
            <SheetHeader>
              <SheetTitle className="font-display">Relationship</SheetTitle>
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
              <div className="mt-4 space-y-4">
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Label</label>
                  <Input
                    defaultValue={selectedRel.label ?? ''}
                    onBlur={(e) =>
                      onUpdateRelationship?.(selectedRel.id, { label: e.target.value || null })
                    }
                  />
                </div>
                <div>
                  <label className="mb-1 block text-xs text-muted-foreground">Technology</label>
                  <Input
                    defaultValue={selectedRel.technology ?? ''}
                    onBlur={(e) =>
                      onUpdateRelationship?.(selectedRel.id, { technology: e.target.value || null })
                    }
                  />
                </div>
              </div>
            )}
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}
