import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createFileRoute, Link } from '@tanstack/react-router'
import { TopBar } from '@/components/shell/top-bar'
import { DetailPanel } from '@/components/shell/detail-panel'
import { LeftToolbar } from '@/components/shell/left-toolbar'
import { StatusBar } from '@/components/shell/status-bar'
import { FilesFlyout } from '@/components/shell/files-flyout'
import { CatalogFlyout } from '@/components/shell/catalog-flyout'
import { CreateFlyout } from '@/components/shell/create-flyout'
import { AddElementDialog } from '@/components/shell/add-element-dialog'
import { ReviewBar } from '@/components/review/review-bar'
import { ScopeNote } from '@/components/branch/scope-note'
import { CanvasView } from '@/components/canvas/canvas-view'
import { DocEditor } from '@/components/docs/doc-editor'
import { CommandPalette } from '@/components/command-palette/command-palette'
import { TraceDock } from '@/components/trace/trace-dock'
import { ContractsFlyout } from '@/components/trace/contracts-flyout'
import { ScenariosFlyout } from '@/components/trace/scenarios-flyout'
import { ImportFlyout } from '@/components/import/import-flyout'
import { AssistantFlyout } from '@/components/assistant/assistant-flyout'
import { PromisesFlyout } from '@/components/promises/promises-flyout'
import { BuildFlyout } from '@/components/build/build-flyout'
import { StressFlyout } from '@/components/stress/stress-flyout'
import { CostsFlyout } from '@/components/costs/costs-flyout'
import { ReviewFlyout } from '@/components/design-review/review-flyout'
import { useRunScenario, useScenarios } from '@/hooks/use-scenarios'
import { useTraceStore } from '@/stores/trace-store'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { useAddElement, useUpdateElement, useRemoveElement } from '@/hooks/use-elements'
import { useAddRelationship, useUpdateRelationship, useRemoveRelationship } from '@/hooks/use-relationships'
import { isProjectRefusal, refuseWrite, useModel, useProject, writeGuard } from '@/hooks/use-model'
import { useViews, useView, useCreateView, useSaveLayout } from '@/hooks/use-views'
import { useDocs, useCreateDoc } from '@/hooks/use-docs'
import { patchApiProjectsByProjectIdDocsByDocId } from '@/api/generated'
import { useSpaces } from '@/hooks/use-spaces'
import { useSchemas } from '@/hooks/use-schemas'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { useDraftStore } from '@/stores/draft-store'
import { useUndoStore } from '@/stores/undo-store'
import type { DiffState } from '@/stores/draft-store'
import { computeLayout } from '@/lib/layout'
import { canContain, pathTo, validKindsForContext } from '@/lib/containment-rules'
import { isPlainShortcut } from '@/lib/shortcuts'
import { buildDuplicateSet } from '@/lib/duplicate'
import { Skeleton } from '@/components/ui/skeleton'
import { Button } from '@/components/ui/button'

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectPage,
})

// The workbench opens only on a project that the person can read. Before that nothing of it
// runs, so an unknown ID reads no model and creates no view.
function ProjectPage() {
  const { projectId } = Route.useParams()
  const { data: project, error } = useProject(projectId)

  if (error) {
    const refused = isProjectRefusal(error)
    return (
      <main className="flex h-screen items-center justify-center bg-background px-4">
        <div className="max-w-sm text-center" role="alert">
          <h1 className="text-base font-semibold">{refused ? 'Project not found or you have no access' : 'The project did not load'}</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            {refused
              ? 'No project with this address exists in a workspace you are a member of.'
              : error.message}
          </p>
          <Button asChild size="sm" className="mt-4">
            <Link to="/">Back to projects</Link>
          </Button>
        </div>
      </main>
    )
  }

  if (!project) {
    return (
      <div className="flex h-screen items-center justify-center" aria-busy="true">
        <Skeleton className="h-8 w-48" />
      </div>
    )
  }

  return <ProjectWorkspace />
}

function ProjectWorkspace() {
  const { projectId } = Route.useParams()
  const { selection, activeDraftStreamId, isReviewMode, reviewData } = useDraftStore()
  // The model of the selected branch or revision. Each tool panel gets the branch as branchId.
  const { elements, relationships, messages, operations, channels, subscriptions, isLoading: modelLoading } = useModel(projectId)
  const contractBranchId = activeDraftStreamId ?? undefined
  const { data: project } = useProject(projectId)
  // A named revision never changes, and a viewer changes nothing.
  const readOnly = selection.kind === 'revision' || project?.yourRole === 'Viewer'
  // The selected model belongs to this project. Another project starts on its own main.
  useEffect(() => () => useDraftStore.getState().reset(), [projectId])
  const scenarioReferences = useMemo<ScenarioReference[]>(() => {
    const elementName = (id: string) => elements?.find((e) => e.id === id)?.name ?? 'Unknown element'
    const channelName = (id: string) => channels?.find((c) => c.id === id)?.name ?? 'Unknown channel'
    return [
      ...(elements ?? []).map((e) => ({ group: 'Elements' as const, id: e.id, label: e.name, kind: e.kind })),
      ...(operations ?? []).map((o) => ({ group: 'Operations' as const, id: o.id, label: `${o.name} · ${elementName(o.ownerId)}` })),
      ...(channels ?? []).map((c) => ({ group: 'Channels' as const, id: c.id, label: c.name })),
      ...(subscriptions ?? []).map((x) => ({ group: 'Subscriptions' as const, id: x.id, label: `${elementName(x.consumerId)} on ${channelName(x.channelId)}` })),
      ...(messages ?? []).map((m) => ({ group: 'Messages' as const, id: m.id, label: m.name })),
    ]
  }, [elements, operations, channels, subscriptions, messages])
  // The review shows a cited contract on the canvas through the element that owns it.
  const reviewElementIds = useMemo(() => (elements ?? []).map((e) => e.id), [elements])
  const reviewOwners = useMemo<Record<string, string>>(() => Object.fromEntries([
    ...(operations ?? []).map((o) => [o.id, o.ownerId]),
    ...(subscriptions ?? []).map((x) => [x.id, x.consumerId]),
    ...(messages ?? []).map((m) => [m.id, m.producerId]),
  ]), [operations, subscriptions, messages])
  const referenceLabels = useMemo(() => Object.fromEntries(scenarioReferences.map((r) => [r.id, r.label])), [scenarioReferences])
  const runScenario = useRunScenario(projectId, contractBranchId)
  const { data: scenarioList } = useScenarios(projectId)
  const playingScenarioId = useTraceStore((s) => s.scenarioId)
  const { data: schemas } = useSchemas(projectId)
  const { data: views } = useViews(projectId)
  const { data: docs } = useDocs(projectId)
  const projectName = project?.name ?? 'Project'
  const createDoc = useCreateDoc(projectId)
  const { data: spaces } = useSpaces(projectId)
  const { activeViewId, activeDocId } = useShellStore()
  const { data: activeView } = useView(projectId, activeViewId)
  // Every write of this page goes through the guard: a read-only model takes none of them.
  const guard = writeGuard(readOnly)
  const addElement = guard(useAddElement(projectId, activeDraftStreamId))
  const updateElement = guard(useUpdateElement(projectId, activeDraftStreamId))
  const removeElement = guard(useRemoveElement(projectId, activeDraftStreamId))
  const addRelationship = guard(useAddRelationship(projectId, activeDraftStreamId))
  const updateRelationship = guard(useUpdateRelationship(projectId, activeDraftStreamId))
  const removeRelationship = guard(useRemoveRelationship(projectId, activeDraftStreamId))
  const { saveLayout: writeLayout } = useSaveLayout(projectId, activeViewId)
  const saveLayout = useCallback<typeof writeLayout>((positions) => (readOnly ? refuseWrite() : writeLayout(positions)), [readOnly, writeLayout])
  const createView = useCreateView(projectId)
  const autoViewRef = useRef(false)

  // The Add Element dialog, with the parent and the kind it starts with. Null when it is closed.
  const [addElementRequest, setAddElementRequest] = useState<{ parentId: string | null; kind?: string } | null>(null)
  const [searchOpen, setSearchOpen] = useState(false)

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
    if (activeViewId || activeDocId || autoViewRef.current) return
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
  }, [activeViewId, activeDocId, views])

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

  // The parent is a fact of the model: the command carries it. Showing the parent's inside
  // is view state only, so the new child is on the canvas when the command returns.
  const handleCreateElement = (name: string, kind: string, parentId: string | null) => {
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => addElement.mutate({ id, kind, name, parentId: parentId ?? undefined }),
      undo: () => removeElement.mutate(id),
    })
    if (parentId) {
      useCanvasStore.getState().showInside(
        pathTo(elements ?? [], parentId).map((e) => ({ elementId: e.id, name: e.name, kind: e.kind })),
      )
    }
  }

  // Without a parent the dialog starts on the level the canvas shows.
  const handleOpenCreateDialog = (parentId?: string | null, kind?: string) => {
    setAddElementRequest({ parentId: parentId === undefined ? useCanvasStore.getState().currentRootId : parentId, kind })
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

  const handleAddElementAtPosition = (kind: string, parentId?: string | null) => {
    handleCreateElement(`New ${kind}`, kind, parentId === undefined ? useCanvasStore.getState().currentRootId : parentId)
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

  const handleNodeResize = useCallback((id: string, width: number, height: number) => {
    const positions = activeView?.positions ?? []
    const existing = positions.find((p) => p.elementId === id)
    if (!existing) return
    const updated = positions.map((p) => (p.elementId === id ? { ...p, width, height } : p))
    saveLayout(updated)
  }, [activeView, saveLayout])

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

  const handleCreateDiagram = useCallback((name: string) => {
    const id = crypto.randomUUID()
    createView.mutate({ id, name }, {
      onSuccess: () => useShellStore.getState().setActiveView(id),
    })
  }, [createView])

  const handleCreateDoc = useCallback((title: string, content: string, type?: string) => {
    const id = crypto.randomUUID()
    const draftId = useDraftStore.getState().activeDraftId
    createDoc.mutate(
      { id, title, draftId: draftId ?? undefined, type },
      {
        onSuccess: () => {
          if (content) {
            patchApiProjectsByProjectIdDocsByDocId({
              path: { projectId, docId: id },
              body: { title: null, content, spaceId: null, elementId: null, setSpaceId: false, setElementId: false },
            }).finally(() => {
              useShellStore.getState().openDoc(id)
            })
          } else {
            useShellStore.getState().openDoc(id)
          }
        },
      },
    )
  }, [projectId, createDoc])

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const meta = e.metaKey || e.ctrlKey
      const target = e.target as HTMLElement | null
      const inInput = target?.closest?.('input, textarea, select, [contenteditable="true"]')
      // A read-only model takes no key that writes: undo, redo, duplicate, tidy, new element.
      const writes = (meta && (e.key === 'z' || e.key === 'd')) || (!meta && (e.key === 't' || e.key === 'n' || e.shiftKey))
      if (readOnly && writes && !inInput) return

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
      if (meta && e.key === 'f') {
        e.preventDefault()
        setSearchOpen(true)
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
      // A key without Ctrl, Cmd or Alt. It runs only when no field, dialog or menu reads the
      // key, and it is marked as used: N opens a dialog whose name field takes the focus
      // before the key is typed, and the letter would go into it.
      if (isPlainShortcut(e)) {
        const lenses = { '1': 'status', '2': 'metadata', '3': 'messages', '4': 'facts' } as const
        const kinds: Record<string, string> = { S: 'system', P: 'actor', A: 'app', D: 'store', X: 'component' }
        if (e.key in lenses) {
          e.preventDefault()
          useCanvasStore.getState().toggleLens(lenses[e.key as keyof typeof lenses])
        } else if (e.key === 't') {
          e.preventDefault()
          handleTidy()
        } else if (e.key === 'n') {
          e.preventDefault()
          handleOpenCreateDialog()
        } else if (e.shiftKey && kinds[e.key]) {
          e.preventDefault()
          handleShortcutCreate(kinds[e.key])
        }
      }
      // Escape takes one layer. A dialog, the palette or an inline editor that used the key
      // marked it as handled, and an open flyout closes first (LeftToolbar). Only an Escape
      // that nothing above used clears the selection.
      if (e.key === 'Escape' && !e.defaultPrevented && !useShellStore.getState().activeFlyout) {
        useShellStore.getState().selectElement(null)
      }
    }

    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [elements, relationships, handleShortcutCreate, handleSmartDuplicate, readOnly])

  const activeFlyout = useShellStore((s) => s.activeFlyout)

  useEffect(() => {
    if (activeFlyout === 'search') {
      useShellStore.getState().closeFlyout()
      useShellStore.getState().toggleCommandPalette()
    }
  }, [activeFlyout])

  if (modelLoading) {
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
      <TopBar projectId={projectId} projectName={projectName} project={project} activeDoc={activeDocForBreadcrumb} />
      <div className="flex flex-1 overflow-hidden">
        <LeftToolbar>
          <div className="flex h-full flex-col">
          {/* Says which model the open panel reads when the workbench shows another model than main. */}
          <ScopeNote flyout={activeFlyout} />
          <div className="min-h-0 flex-1">
          {activeFlyout === 'files' && (
            <FilesFlyout
              views={views ?? []}
              docs={(docs ?? []).map((d) => ({ id: d.id, title: d.title, type: d.type ?? null }))}
              model={project && { projectId, projectName: project.name, canImport: project.yourRole !== 'Viewer' }}
            />
          )}
          {activeFlyout === 'catalog' && (
            <CatalogFlyout
              elements={(elements ?? []).map((e) => ({ id: e.id, kind: e.kind, name: e.name }))}
              onAddElement={handleAddElementAtPosition}
            />
          )}
          {activeFlyout === 'create' && (
            <CreateFlyout
              onCreateDiagram={handleCreateDiagram}
              onCreateDoc={handleCreateDoc}
              onAddElement={() => handleOpenCreateDialog()}
            />
          )}
          {activeFlyout === 'contracts' && (
            <ContractsFlyout projectId={projectId} branchId={contractBranchId} elements={elements ?? []} />
          )}
          {activeFlyout === 'scenarios' && (
            <ScenariosFlyout projectId={projectId} branchId={contractBranchId} references={scenarioReferences} />
          )}
          {activeFlyout === 'import' && <ImportFlyout projectId={projectId} owners={reviewOwners} />}
          {activeFlyout === 'promises' && <PromisesFlyout projectId={projectId} references={scenarioReferences} scenarios={scenarioList ?? []} owners={reviewOwners} />}
          {activeFlyout === 'review' && (
            <ReviewFlyout projectId={projectId} branchId={contractBranchId} references={scenarioReferences} elementIds={reviewElementIds} owners={reviewOwners} />
          )}
          {activeFlyout === 'build' && <BuildFlyout projectId={projectId} elements={elements ?? []} selection={() => selectedNodeIdsRef.current} />}
          {activeFlyout === 'stress' && <StressFlyout projectId={projectId} branchId={contractBranchId} scenarios={scenarioList ?? []} />}
          {activeFlyout === 'costs' && <CostsFlyout projectId={projectId} branchId={contractBranchId} references={scenarioReferences} />}
          {activeFlyout === 'assistant' && (
            <AssistantFlyout
              projectId={projectId}
              branchId={contractBranchId}
              labels={referenceLabels}
              playingScenarioId={playingScenarioId}
              onPlayScenario={(scenarioId) => runScenario.mutate(scenarioId, { onSuccess: (run) => useTraceStore.getState().load(run, scenarioId) })}
            />
          )}
          {activeFlyout === 'search' && null /* ⌘K handles search for now */}
          </div>
          </div>
        </LeftToolbar>
        <main className="relative flex-1">
          {activeDocId ? (
            <DocEditor projectId={projectId} docId={activeDocId} />
          ) : (
            <CanvasView
              elements={(elements ?? []).map((e) => ({ ...e, ownerId: e.ownerId ?? null }))}
              relationships={relationships ?? []}
              positions={positions}
              diffStateMap={diffStateMap}
              isReviewMode={isReviewMode}
              readOnly={readOnly}
              messages={messages ?? []}
              onFitViewReady={handleFitViewReady}
              onViewportCenterReady={handleViewportCenterReady}
              onAddElement={() => handleOpenCreateDialog()}
              onAddChild={handleOpenCreateDialog}
              onConnect={handleConnect}
              onNodeDragStop={handleNodeDragStop}
              onNodeResize={handleNodeResize}
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
                // An empty system or app opens too: its level is where the first child is added.
                if (canContain(el.kind)) {
                  useCanvasStore.getState().drillInto({
                    elementId: el.id,
                    name: el.name,
                    kind: el.kind,
                  })
                }
              }}
              searchOpen={searchOpen}
              onSearchClose={() => setSearchOpen(false)}
            />
          )}
        </main>
        <DetailPanel
          projectId={projectId}
          elements={elements ?? []}
          relationships={relationships ?? []}
          messages={messages ?? []}
          schemas={schemas ?? []}
          readOnly={readOnly}
          onUpdateElement={(id, fields) => updateElement.mutate({ id, ...fields })}
          onUpdateRelationship={(id, fields) => updateRelationship.mutate({ id, ...fields })}
          onDeleteElement={(id) => removeElement.mutate(id)}
          onDeleteRelationship={(id) => removeRelationship.mutate(id)}
          onAddChild={handleOpenCreateDialog}
        />
      </div>
      {!activeDocId && <TraceDock />}
      {activeDocId ? null : (
        isReviewMode ? (
          <ReviewBar projectId={projectId} />
        ) : (
          <StatusBar onTidy={handleTidy} onZoomToFit={() => fitViewRef.current?.()} />
        )
      )}
      <CommandPalette
        elements={elements ?? []}
        onAddElement={() => handleOpenCreateDialog()}
        onTidy={handleTidy}
        onZoomToFit={() => fitViewRef.current?.()}
      />

      {addElementRequest && (
        <AddElementDialog
          parentId={addElementRequest.parentId}
          kind={addElementRequest.kind}
          elements={elements ?? []}
          pending={addElement.isPending}
          onClose={() => setAddElementRequest(null)}
          onAdd={(name, kind, parentId) => {
            handleCreateElement(name, kind, parentId)
            setAddElementRequest(null)
          }}
        />
      )}
    </div>
  )
}
