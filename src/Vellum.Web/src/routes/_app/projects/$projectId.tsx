import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { Navigator } from '@/components/shell/navigator'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LensBar } from '@/components/shell/lens-bar'
import { ReviewBar } from '@/components/review/review-bar'
import { CanvasView } from '@/components/canvas/canvas-view'
import { DocEditor } from '@/components/docs/doc-editor'
import { CommandPalette } from '@/components/command-palette/command-palette'
import { useElements, useAddElement, useUpdateElement, useRemoveElement } from '@/hooks/use-elements'
import { useRelationships, useAddRelationship, useUpdateRelationship, useRemoveRelationship } from '@/hooks/use-relationships'
import { useViews, useView, useCreateView, useSaveLayout } from '@/hooks/use-views'
import { useDocs } from '@/hooks/use-docs'
import { useSpaces } from '@/hooks/use-spaces'
import { useMessages } from '@/hooks/use-messages'
import { useSchemas } from '@/hooks/use-schemas'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { useDraftStore } from '@/stores/draft-store'
import { useUndoStore } from '@/stores/undo-store'
import type { DiffState } from '@/stores/draft-store'
import { useMergePreview } from '@/hooks/use-merge'
import { computeLayout } from '@/lib/layout'
import { validKindsForContext } from '@/lib/containment-rules'
import { buildDuplicateSet } from '@/lib/duplicate'
import { Skeleton } from '@/components/ui/skeleton'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/components/ui/dialog'

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectWorkspace,
})

function ProjectWorkspace() {
  const { projectId } = Route.useParams()
  const { activeDraftId, activeDraftStreamId, isReviewMode, reviewData, enterReviewMode } = useDraftStore()
  const { data: elements, isLoading: elementsLoading } = useElements(projectId, activeDraftStreamId ?? undefined)
  const { data: relationships, isLoading: relsLoading } = useRelationships(projectId, activeDraftStreamId ?? undefined)
  const { data: messages } = useMessages(projectId, activeDraftStreamId ?? undefined)
  const { data: schemas } = useSchemas(projectId)
  const { data: views } = useViews(projectId)
  const { data: docs } = useDocs(projectId)
  const { data: spaces } = useSpaces(projectId)
  const { activeViewId, activeDocId } = useShellStore()
  const { data: activeView } = useView(projectId, activeViewId)
  const addElement = useAddElement(projectId, activeDraftStreamId)
  const updateElement = useUpdateElement(projectId, activeDraftStreamId)
  const removeElement = useRemoveElement(projectId, activeDraftStreamId)
  const addRelationship = useAddRelationship(projectId, activeDraftStreamId)
  const updateRelationship = useUpdateRelationship(projectId, activeDraftStreamId)
  const removeRelationship = useRemoveRelationship(projectId, activeDraftStreamId)
  const { saveLayout } = useSaveLayout(projectId, activeViewId)
  const createView = useCreateView(projectId)
  const mergePreview = useMergePreview(projectId)
  const autoViewRef = useRef(false)

  const [showCreateElement, setShowCreateElement] = useState(false)
  const [newElementName, setNewElementName] = useState('')
  const [newElementKind, setNewElementKind] = useState('system')

  const fitViewRef = useRef<(() => void) | null>(null)
  const handleFitViewReady = useCallback((fn: () => void) => {
    fitViewRef.current = fn
  }, [])

  const selectedNodeIdsRef = useRef<string[]>([])

  const viewportCenterRef = useRef<(() => { x: number; y: number }) | null>(null)
  const handleViewportCenterReady = useCallback((fn: () => { x: number; y: number }) => {
    viewportCenterRef.current = fn
  }, [])

  useEffect(() => {
    if (activeViewId || autoViewRef.current) return
    if (!views) return
    if (views.length > 0) {
      useShellStore.getState().setActiveView(views[0].id)
    } else {
      autoViewRef.current = true
      const id = crypto.randomUUID()
      createView.mutate({ id, name: 'Default' }, {
        onSuccess: () => useShellStore.getState().setActiveView(id),
      })
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeViewId, views])

  const handleReview = async () => {
    if (!activeDraftId) return
    const data = await mergePreview.mutateAsync(activeDraftId)
    enterReviewMode(data)
  }

  // Build a map from entityId -> DiffState for canvas diff colouring
  const diffStateMap = useMemo<Map<string, DiffState>>(() => {
    if (!isReviewMode || !reviewData) return new Map()
    const map = new Map<string, DiffState>()
    for (const change of reviewData.autoResolved) {
      const kind = change.changeKind as DiffState
      map.set(change.entityId, kind === 'added' || kind === 'removed' || kind === 'modified' ? kind : 'modified')
    }
    for (const conflict of reviewData.conflicts) {
      map.set(conflict.entityId, 'conflict')
    }
    return map
  }, [isReviewMode, reviewData])

  const handleTidy = async () => {
    if (!elements || !relationships) return
    const newPositions = await computeLayout(elements, relationships)
    saveLayout(newPositions)
  }

  const handleCreateElement = (name: string, kind: string) => {
    const { currentRootId } = useCanvasStore.getState()
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => addElement.mutate({ id, kind, name, parentId: currentRootId ?? undefined }),
      undo: () => removeElement.mutate(id),
    })
  }

  const handleOpenCreateDialog = () => {
    const { currentRootId } = useCanvasStore.getState()
    setNewElementKind(currentRootId ? 'app' : 'system')
    setNewElementName('')
    setShowCreateElement(true)
  }

  const handleConnect = (source: string, target: string) => {
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: 'Add relationship',
      execute: () => addRelationship.mutate({ id, fromId: source, toId: target }),
      undo: () => removeRelationship.mutate(id),
    })
  }

  const handleBulkDelete = (ids: string[]) => {
    if (ids.length > 3 && !window.confirm(`Delete ${ids.length} elements?`)) return
    const snapshots = ids
      .map((id) => elements?.find((e) => e.id === id))
      .filter((el): el is NonNullable<typeof el> => el != null)

    useUndoStore.getState().execute({
      label: `Delete ${ids.length} elements`,
      execute: () => ids.forEach((id) => removeElement.mutate(id)),
      undo: () => snapshots.forEach((el) =>
        addElement.mutate({
          id: el.id,
          kind: el.kind,
          name: el.name,
          description: el.description ?? undefined,
          technology: el.technology ?? undefined,
          parentId: el.parentId ?? undefined,
          status: el.status,
          tags: el.tags,
        }),
      ),
    })
  }

  const handleBulkStatusChange = (ids: string[], status: string) => {
    const snapshots = ids
      .map((id) => elements?.find((e) => e.id === id))
      .filter((el): el is NonNullable<typeof el> => el != null)

    useUndoStore.getState().execute({
      label: `Set ${ids.length} elements to ${status}`,
      execute: () => ids.forEach((id) => updateElement.mutate({ id, status })),
      undo: () => snapshots.forEach((el) => updateElement.mutate({ id: el.id, status: el.status })),
    })
  }

  const handleBulkAddTag = (ids: string[], tag: string) => {
    const snapshots = ids
      .map((id) => elements?.find((e) => e.id === id))
      .filter((el): el is NonNullable<typeof el> => el != null)

    useUndoStore.getState().execute({
      label: `Tag ${ids.length} elements "${tag}"`,
      execute: () => snapshots.forEach((el) => {
        if (!el.tags.includes(tag)) {
          updateElement.mutate({ id: el.id, tags: [...el.tags, tag] })
        }
      }),
      undo: () => snapshots.forEach((el) => updateElement.mutate({ id: el.id, tags: el.tags })),
    })
  }

  const handleSmartDuplicate = useCallback((ids: string[]) => {
    if (!elements || !relationships) return
    const positions = activeView?.positions ?? []
    const dupSet = buildDuplicateSet(ids, elements, relationships, positions)
    if (dupSet.elements.length === 0) return

    const allNewElementIds = dupSet.elements.map((e) => e.newId)
    const allNewRelIds = dupSet.relationships.map((r) => r.newId)

    useUndoStore.getState().execute({
      label: `Duplicate ${dupSet.elements.length} element${dupSet.elements.length > 1 ? 's' : ''}`,
      execute: () => {
        for (const el of dupSet.elements) {
          addElement.mutate({
            id: el.newId,
            kind: el.kind,
            name: el.name,
            description: el.description ?? undefined,
            technology: el.technology ?? undefined,
            parentId: el.parentId ?? undefined,
            status: el.status,
            tags: el.tags,
          })
        }
        for (const rel of dupSet.relationships) {
          addRelationship.mutate({
            id: rel.newId,
            fromId: rel.fromId,
            toId: rel.toId,
            label: rel.label ?? undefined,
            technology: rel.technology ?? undefined,
          })
        }
        const currentPositions = activeView?.positions ?? []
        saveLayout([...currentPositions, ...dupSet.positions])
      },
      undo: () => {
        allNewRelIds.forEach((id) => removeRelationship.mutate(id))
        allNewElementIds.forEach((id) => removeElement.mutate(id))
      },
    })
  }, [elements, relationships, activeView, addElement, addRelationship, removeElement, removeRelationship, saveLayout])

  const handleDuplicateElement = useCallback((id: string) => {
    handleSmartDuplicate([id])
  }, [handleSmartDuplicate])

  const handleReverseRelationship = (id: string) => {
    const rel = relationships?.find((r) => r.id === id)
    if (!rel) return
    removeRelationship.mutate(id, {
      onSuccess: () => addRelationship.mutate({
        id: crypto.randomUUID(),
        fromId: rel.toId,
        toId: rel.fromId,
        label: rel.label ?? undefined,
        technology: rel.technology ?? undefined,
      }),
    })
  }

  const handleDeleteRelationship = (id: string) => {
    const rel = relationships?.find((r) => r.id === id)
    if (!rel) return
    if (!window.confirm('Delete this relationship?')) return

    useUndoStore.getState().execute({
      label: 'Delete relationship',
      execute: () => removeRelationship.mutate(id),
      undo: () => addRelationship.mutate({
        id: rel.id,
        fromId: rel.fromId,
        toId: rel.toId,
        label: rel.label ?? undefined,
        technology: rel.technology ?? undefined,
      }),
    })
  }

  const handleDeleteElement = (id: string) => {
    const el = elements?.find((e) => e.id === id)
    if (!el) return
    if (!window.confirm(`Delete "${el.name}"?`)) return

    useUndoStore.getState().execute({
      label: `Delete ${el.name}`,
      execute: () => removeElement.mutate(id),
      undo: () => addElement.mutate({
        id: el.id,
        kind: el.kind,
        name: el.name,
        description: el.description ?? undefined,
        technology: el.technology ?? undefined,
        parentId: el.parentId ?? undefined,
        status: el.status,
        tags: el.tags,
      }),
    })
  }

  const handleAddElementAtPosition = (kind: string) => {
    const { currentRootId } = useCanvasStore.getState()
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => addElement.mutate({
        id,
        kind,
        name: `New ${kind}`,
        parentId: currentRootId ?? undefined,
      }),
      undo: () => removeElement.mutate(id),
    })
  }

  const handleConnectToBlank = (kind: string, position: { x: number; y: number }, sourceId: string) => {
    const { currentRootId } = useCanvasStore.getState()
    const elementId = crypto.randomUUID()
    const relationshipId = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind} with connection`,
      execute: () => {
        addElement.mutate({
          id: elementId,
          kind,
          name: `New ${kind}`,
          parentId: currentRootId ?? undefined,
        })
        addRelationship.mutate({
          id: relationshipId,
          fromId: sourceId,
          toId: elementId,
        })
        const positions = activeView?.positions ?? []
        saveLayout([...positions, { elementId, x: position.x, y: position.y }])
      },
      undo: () => {
        removeRelationship.mutate(relationshipId)
        removeElement.mutate(elementId)
      },
    })
  }

  const handleAddElementAtFlowPosition = (kind: string, position: { x: number; y: number }) => {
    const { currentRootId } = useCanvasStore.getState()
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => {
        addElement.mutate({
          id,
          kind,
          name: `New ${kind}`,
          parentId: currentRootId ?? undefined,
        })
        const positions = activeView?.positions ?? []
        saveLayout([...positions, { elementId: id, x: position.x, y: position.y }])
      },
      undo: () => removeElement.mutate(id),
    })
  }

  const handleNodeDragStop = (id: string, x: number, y: number) => {
    const current = activeView?.positions ?? []
    const oldPos = current.find((p) => p.elementId === id)
    const oldX = oldPos?.x ?? 0
    const oldY = oldPos?.y ?? 0

    const applyPosition = (px: number, py: number) => {
      const positions = activeView?.positions ?? []
      const updated = positions.some((p) => p.elementId === id)
        ? positions.map((p) => (p.elementId === id ? { ...p, x: px, y: py } : p))
        : [...positions, { elementId: id, x: px, y: py }]
      saveLayout(updated)
    }

    useUndoStore.getState().execute({
      label: 'Move element',
      execute: () => applyPosition(x, y),
      undo: () => applyPosition(oldX, oldY),
    })
  }

  const handleRenameElement = (id: string, newName: string) => {
    const el = elements?.find((e) => e.id === id)
    if (!el) return
    const oldName = el.name
    useUndoStore.getState().execute({
      label: `Rename to "${newName}"`,
      execute: () => updateElement.mutate({ id, name: newName }),
      undo: () => updateElement.mutate({ id, name: oldName }),
    })
  }

  const handleShortcutCreate = useCallback((kind: string) => {
    const { currentRootId } = useCanvasStore.getState()
    const parentKind = currentRootId
      ? elements?.find((e) => e.id === currentRootId)?.kind ?? null
      : null
    const valid = validKindsForContext(parentKind)
    if (!valid.includes(kind)) return

    const id = crypto.randomUUID()
    const position = viewportCenterRef.current?.() ?? { x: 0, y: 0 }

    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => {
        addElement.mutate({
          id,
          kind,
          name: `New ${kind}`,
          parentId: currentRootId ?? undefined,
        })
        const positions = activeView?.positions ?? []
        saveLayout([...positions, { elementId: id, x: position.x, y: position.y }])
      },
      undo: () => removeElement.mutate(id),
    })
  }, [elements, activeView, addElement, removeElement, saveLayout])

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const meta = e.metaKey || e.ctrlKey
      const target = e.target as HTMLElement | null
      const inInput = target?.closest?.('input, textarea, select, [contenteditable="true"]')

      if (meta && e.key === 'z' && !e.shiftKey && !inInput) {
        e.preventDefault()
        useUndoStore.getState().undo()
        return
      }
      if (meta && e.key === 'z' && e.shiftKey && !inInput) {
        e.preventDefault()
        useUndoStore.getState().redo()
        return
      }
      if (meta && e.key === 'k') {
        e.preventDefault()
        useShellStore.getState().toggleCommandPalette()
      }
      if (meta && e.key === 'a' && !inInput) {
        e.preventDefault()
        // React Flow handles select-all natively when selectionOnDrag is enabled
        // We just need to prevent the browser's select-all
      }
      if (meta && e.key === 'd' && !inInput) {
        e.preventDefault()
        const ids = selectedNodeIdsRef.current.length > 0
          ? selectedNodeIdsRef.current
          : useShellStore.getState().selectedElementId
            ? [useShellStore.getState().selectedElementId!]
            : []
        if (ids.length > 0) {
          handleSmartDuplicate(ids)
        }
      }
      if (e.key === '1' && !inInput) {
        useCanvasStore.getState().toggleLens('status')
      }
      if (e.key === '2' && !inInput) {
        useCanvasStore.getState().toggleLens('metadata')
      }
      if (e.key === '3' && !inInput) {
        useCanvasStore.getState().toggleLens('messages')
      }
      if (e.key === 't' && !inInput) {
        handleTidy()
      }
      if (e.key === 'n' && !inInput) {
        handleOpenCreateDialog()
      }
      if (e.shiftKey && !meta && !inInput) {
        const shortcutMap: Record<string, string> = {
          S: 'system',
          P: 'actor',
          A: 'app',
          D: 'store',
          X: 'component',
        }
        const kind = shortcutMap[e.key]
        if (kind) {
          e.preventDefault()
          handleShortcutCreate(kind)
        }
      }
      if (e.key === 'Escape') {
        useShellStore.getState().closeCommandPalette()
        useShellStore.getState().selectElement(null)
      }
    }

    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [elements, relationships, handleShortcutCreate, handleSmartDuplicate])

  if (elementsLoading || relsLoading) {
    return (
      <div className="flex h-screen flex-col">
        <TopBar projectId={projectId} />
        <div className="flex flex-1 items-center justify-center">
          <Skeleton className="h-8 w-48" />
        </div>
      </div>
    )
  }

  const positions = activeView?.positions ?? []

  const activeDocData = activeDocId ? (docs ?? []).find((d) => d.id === activeDocId) : null
  const activeDocSpaceName = activeDocData?.spaceId
    ? (spaces ?? []).find((s) => s.id === activeDocData.spaceId)?.name ?? null
    : null
  const activeDocForBreadcrumb = activeDocData
    ? { id: activeDocData.id, title: activeDocData.title, spaceName: activeDocSpaceName }
    : null

  return (
    <div className="flex h-screen flex-col">
      <TopBar projectId={projectId} activeDoc={activeDocForBreadcrumb} />
      <div className="flex flex-1 overflow-hidden">
        <Navigator
          projectId={projectId}
          elements={elements ?? []}
          views={views ?? []}
          spaces={spaces ?? []}
          docs={(docs ?? []).map((d) => ({ id: d.id, title: d.title, spaceId: d.spaceId, type: d.type ?? null }))}
        />
        <main className="relative flex-1">
          {activeDraftId && !isReviewMode && !activeDocId && (
            <div className="absolute right-3 top-3 z-10">
              <Button
                size="sm"
                className="h-7 text-xs"
                onClick={handleReview}
                disabled={mergePreview.isPending}
              >
                {mergePreview.isPending ? 'Loading…' : 'Review'}
              </Button>
            </div>
          )}
          {activeDocId ? (
            <DocEditor projectId={projectId} docId={activeDocId} />
          ) : (
            <CanvasView
              elements={(elements ?? []).map((e) => ({ ...e, ownerId: e.ownerId ?? null }))}
              relationships={relationships ?? []}
              positions={positions}
              diffStateMap={diffStateMap}
              isReviewMode={isReviewMode}
              messages={messages ?? []}
              onFitViewReady={handleFitViewReady}
              onViewportCenterReady={handleViewportCenterReady}
              onAddElement={handleOpenCreateDialog}
              onConnect={handleConnect}
              onNodeDragStop={handleNodeDragStop}
              onBulkDelete={handleBulkDelete}
              onBulkStatusChange={handleBulkStatusChange}
              onBulkAddTag={handleBulkAddTag}
              onDuplicateElement={handleDuplicateElement}
              onSelectionChange={(ids) => { selectedNodeIdsRef.current = ids }}
              onDeleteElement={handleDeleteElement}
              onReverseRelationship={handleReverseRelationship}
              onDeleteRelationship={handleDeleteRelationship}
              onUpdateRelationship={(id, fields) => updateRelationship.mutate({ id, ...fields })}
              onAddElementAtPosition={handleAddElementAtPosition}
              onTidy={handleTidy}
              onRenameElement={handleRenameElement}
              onConnectToBlank={handleConnectToBlank}
              onAddElementAtFlowPosition={handleAddElementAtFlowPosition}
              onNodeDoubleClick={(elementId) => {
                const el = elements?.find((e) => e.id === elementId)
                if (!el) return
                const children = elements?.filter((e) => e.parentId === elementId)
                if (children && children.length > 0) {
                  useCanvasStore.getState().drillInto({
                    elementId: el.id,
                    name: el.name,
                    kind: el.kind,
                  })
                }
              }}
            />
          )}
        </main>
        <DetailPanel
          projectId={projectId}
          elements={elements ?? []}
          relationships={relationships ?? []}
          messages={messages ?? []}
          schemas={schemas ?? []}
          onUpdateElement={(id, fields) => updateElement.mutate({ id, ...fields })}
          onUpdateRelationship={(id, fields) => updateRelationship.mutate({ id, ...fields })}
          onDeleteElement={(id) => removeElement.mutate(id)}
          onDeleteRelationship={(id) => removeRelationship.mutate(id)}
        />
      </div>
      {isReviewMode ? (
        <ReviewBar projectId={projectId} />
      ) : (
        <LensBar onTidy={handleTidy} onZoomToFit={() => fitViewRef.current?.()} />
      )}
      <CommandPalette
        elements={elements ?? []}
        onAddElement={handleOpenCreateDialog}
        onTidy={handleTidy}
        onZoomToFit={() => fitViewRef.current?.()}
      />

      <Dialog open={showCreateElement} onOpenChange={setShowCreateElement}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Add Element</DialogTitle>
          </DialogHeader>
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (newElementName.trim()) {
                handleCreateElement(newElementName.trim(), newElementKind)
                setShowCreateElement(false)
              }
            }}
          >
            <div className="space-y-3">
              <div>
                <label className="mb-1 block text-xs font-medium text-muted-foreground">Kind</label>
                <select
                  value={newElementKind}
                  onChange={(e) => setNewElementKind(e.target.value)}
                  className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm"
                >
                  <option value="actor">Actor</option>
                  <option value="system">System</option>
                  <option value="app">App</option>
                  <option value="store">Store</option>
                  <option value="component">Component</option>
                </select>
              </div>
              <Input
                placeholder="Element name"
                value={newElementName}
                onChange={(e) => setNewElementName(e.target.value)}
                autoFocus
              />
            </div>
            <DialogFooter className="mt-4">
              <Button type="submit" disabled={!newElementName.trim() || addElement.isPending}>
                {addElement.isPending ? 'Adding...' : 'Add'}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  )
}
