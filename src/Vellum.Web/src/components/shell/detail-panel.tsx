import { useShellStore } from '@/stores/shell-store'
import { Sheet, SheetContent, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { kindColor } from '@/lib/kind-colors'

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

interface DetailPanelProps {
  elements: ElementData[]
  relationships: RelationshipData[]
  onUpdateElement?: (id: string, fields: Partial<ElementData>) => void
  onUpdateRelationship?: (id: string, fields: Partial<RelationshipData>) => void
}

export function DetailPanel({
  elements,
  relationships,
  onUpdateElement,
  onUpdateRelationship,
}: DetailPanelProps) {
  const { detailPanelOpen, selectedElementId, selectedRelationshipId, selectElement, selectRelationship } =
    useShellStore()

  const selectedElement = elements.find((e) => e.id === selectedElementId)
  const selectedRel = relationships.find((r) => r.id === selectedRelationshipId)

  const handleClose = () => {
    selectElement(null)
    selectRelationship(null)
  }

  return (
    <Sheet open={detailPanelOpen} onOpenChange={(open) => !open && handleClose()}>
      <SheetContent side="right" className="w-80 border-l border-border bg-card">
        {selectedElement && (
          <>
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
          </>
        )}

        {selectedRel && (
          <>
            <SheetHeader>
              <SheetTitle className="font-display">Relationship</SheetTitle>
            </SheetHeader>
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
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}
