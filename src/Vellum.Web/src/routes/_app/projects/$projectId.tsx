import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createFileRoute, Link, useNavigate } from '@tanstack/react-router'
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
import { StressDock } from '@/components/stress/stress-dock'
import { CostsFlyout } from '@/components/costs/costs-flyout'
import { ReviewFlyout } from '@/components/design-review/review-flyout'
import { ShareLinksPanel } from '@/components/share/share-dialog'
import { AssistantCanvasButton } from '@/components/assistant/assistant-canvas-button'
import type { AreaAccess } from '@/lib/areas'
import { EmptyProject } from '@/components/start/empty-project'
import { TypeItPanel } from '@/components/start/type-it-panel'
import { designApi, isUnavailable, type FlowProposal } from '@/lib/design-api'
import { useQueryClient } from '@tanstack/react-query'
import type { Element } from '@/hooks/use-elements'
import type { Relationship } from '@/hooks/use-relationships'
import { openThreadMarkers, useThreads } from '@/hooks/use-threads'
import { useRunScenario, useScenarios } from '@/hooks/use-scenarios'
import { useTraceStore } from '@/stores/trace-store'
import type { ScenarioReference } from '@/components/trace/scenario-editor'
import { useAddElement, useUpdateElement, useRemoveElement } from '@/hooks/use-elements'
import { useAddRelationship, useUpdateRelationship, useRemoveRelationship } from '@/hooks/use-relationships'
import { isProjectRefusal, projectErrorBlocks, refuseWrite, useModel, useProject, writeGuard } from '@/hooks/use-model'
import { useFactLabel } from '@/lib/fact-labels'
import { toast } from 'sonner'
import { useViews, useView, useCreateView, useSaveLayout, useSaveEdgeSides, useSaveLineVisibility, useSetHiddenCards, type ViewDetail } from '@/hooks/use-views'
import { lineVisibilityOf, type LineVisibility } from '@/lib/line-visibility'
import { PickDialog } from '@/components/shell/pick-dialog'
import { TypedDeleteDialog } from '@/components/shell/typed-delete-dialog'
import { downloadStructurizr, downloadView } from '@/lib/diagram-export'
import { pasteSet, useClipboardStore } from '@/stores/clipboard-store'
import type { ViewExport } from '@/components/canvas/context-menus/canvas-context-menu'
import { useDocs, useCreateDoc } from '@/hooks/use-docs'
import { useSpaces } from '@/hooks/use-spaces'
import { useSchemas } from '@/hooks/use-schemas'
import { useShellStore } from '@/stores/shell-store'
import { useCanvasStore } from '@/stores/canvas-store'
import { useDraftStore } from '@/stores/draft-store'
import { useUndoStore } from '@/stores/undo-store'
import type { DiffState } from '@/stores/draft-store'
import { computeLayout, placeNew } from '@/lib/layout'
import { parseRoute, type LayoutRoute } from '@/lib/edge-utils'
import { useNeedsYouByEntity, useScanCompletion } from '@/hooks/use-needs-you'
import { cardSize } from '@/lib/card-size'
import { visibleCanvasRect } from '@/lib/canvas-insets'
import { canContain, pathTo, validKindsForContext } from '@/lib/containment-rules'
import { isPlainShortcut } from '@/lib/shortcuts'
import { buildDuplicateSet } from '@/lib/duplicate'
import { Skeleton } from '@/components/ui/skeleton'
import { Button } from '@/components/ui/button'
import { branchesApi, canEditModel } from '@/lib/branches-api'
import { FlowProposalDialog } from '@/components/assistant/flow-proposal-dialog'
import { useGhostStore } from '@/stores/ghost-store'

/**
 * `thread` opens one comment thread, from the inbox or a webhook link; `draft` is the draft it is on.
 * `proposal` opens additions as the ghost layer, and `flowProposal` a proposed flow, from the inbox.
 * `view` opens one view, from a copied link to it.
 */
interface ProjectSearch {
  view?: string
  thread?: string
  draft?: string
  proposal?: string
  flowProposal?: string
}

export const Route = createFileRoute('/_app/projects/$projectId')({
  component: ProjectPage,
  validateSearch: (search: Record<string, unknown>): ProjectSearch => ({
    view: typeof search.view === 'string' ? search.view : undefined,
    thread: typeof search.thread === 'string' ? search.thread : undefined,
    draft: typeof search.draft === 'string' ? search.draft : undefined,
    proposal: typeof search.proposal === 'string' ? search.proposal : undefined,
    flowProposal: typeof search.flowProposal === 'string' ? search.flowProposal : undefined,
  }),
})

// The workbench opens only on a project that the person can read. Before that nothing of it
// runs, so an unknown ID reads no model and creates no view.
function ProjectPage() {
  const { projectId } = Route.useParams()
  const { data: project, error } = useProject(projectId)

  if (projectErrorBlocks(!!project, error)) {
    const refused = isProjectRefusal(error)
    return (
      <main className="flex h-screen items-center justify-center bg-background px-4">
        <div className="max-w-sm text-center" role="alert">
          <h1 className="text-base font-semibold">{refused ? 'Project not found or you have no access' : 'The project did not load'}</h1>
          <p className="mt-2 text-sm text-muted-foreground">
            {refused
              ? 'No project with this address exists in a workspace you are a member of.'
              : error?.message}
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

  return (
    <>
      {/* Each project starts on a fresh workbench: its own view, level and panels. */}
      <ProjectWorkspace key={project.id} />
      {/* A failed refetch keeps what loaded; the query tries again on its own schedule. */}
      {error && (
        <div role="status" className="fixed bottom-10 left-1/2 z-50 max-w-md -translate-x-1/2 rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground shadow-md">
          The project did not refresh ({error.message}). The workbench shows what loaded last.
        </div>
      )}
    </>
  )
}

function ProjectWorkspace() {
  const { projectId } = Route.useParams()
  const { selection, activeDraftStreamId, isReviewMode, reviewData } = useDraftStore()
  // The model of the selected branch or revision. Each tool panel gets the branch as branchId.
  const { elements, relationships, messages, operations, channels, subscriptions, isLoading: modelLoading } = useModel(projectId)
  const contractBranchId = activeDraftStreamId ?? undefined
  // The named revision that the workbench shows. Contracts and Review read it.
  const openRevision = selection.kind === 'revision' ? { id: selection.id, name: selection.name } : null
  const { data: project } = useProject(projectId)
  // A named revision never changes, and a viewer changes nothing.
  const readOnly = selection.kind === 'revision' || !canEditModel(project?.yourRole)
  // The selected model belongs to this project. Another project starts on its own main.
  // The open view, document, panel and canvas level belong to this project too: a project opened
  // from the project menu must not read the view of the last one.
  useEffect(() => () => {
    useDraftStore.getState().reset()
    useShellStore.getState().reset()
    useCanvasStore.getState().reset()
  }, [projectId])
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
  // Comment threads of the branch: main, or the selected draft. The canvas draws a badge per target with open threads.
  const activeDraftId = useDraftStore((s) => s.activeDraftId)
  const { data: threads, isSuccess: threadsLoaded } = useThreads(projectId, activeDraftId)
  const commentMarkers = useMemo(() => openThreadMarkers(threads), [threads])
  useOpenLinkedThread(projectId, threads, threadsLoaded, elements, relationships, messages)
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
  const factLabel = guard(useFactLabel(projectId, activeDraftStreamId))
  const { saveLayout: writeLayout } = useSaveLayout(projectId, activeViewId)
  const saveLayout = useCallback<typeof writeLayout>((positions, routes) => (readOnly ? refuseWrite() : writeLayout(positions, routes)), [readOnly, writeLayout])
  // Which lines the open view draws: layout data of the view, saved for everyone.
  const lineVisibility = useMemo(() => lineVisibilityOf(activeView?.lineVisibility), [activeView])
  const saveLineVisibility = guard(useSaveLineVisibility(projectId, activeViewId))
  const handleLineVisibilityChange = (visibility: LineVisibility) => saveLineVisibility.mutate(visibility)
  const saveEdgeSides = guard(useSaveEdgeSides(projectId, activeViewId))
  const setHiddenCards = guard(useSetHiddenCards(projectId, activeViewId))
  const handleEdgeSidesChange = useCallback(
    (relationshipId: string, sides: { sourceSide: string | null; targetSide: string | null }) => saveEdgeSides.mutate({ relationshipId, ...sides }),
    [saveEdgeSides],
  )
  const createView = useCreateView(projectId)
  const autoViewRef = useRef(false)
  const { view: linkedViewId } = Route.useSearch()
  const navigate = useNavigate()

  // A link to a view opens that view. The address loses the parameter after.
  useEffect(() => {
    if (!linkedViewId || !views) return
    if (views.some((v) => v.id === linkedViewId)) useShellStore.getState().setActiveView(linkedViewId)
    else toast.error('The view of this link is not in the project any more.')
    navigate({ to: '/projects/$projectId', params: { projectId }, search: {}, replace: true })
  }, [linkedViewId, views, navigate, projectId])

  // An accepted ghost keeps its place: the layer saves it in the open view, with the sizes of
  // the cards that it opened. A layer belongs to its project.
  useEffect(() => {
    useGhostStore.getState().setViewPlaceWriter((places, sizes) => {
      const ids = new Set(places.map((p) => p.elementId))
      const current = (activeView?.positions ?? [])
        .filter((p) => !ids.has(p.elementId))
        .map((p) => (sizes.has(p.elementId) ? { ...p, ...sizes.get(p.elementId)! } : p))
      saveLayout([...current, ...places])
    })
    return () => useGhostStore.getState().setViewPlaceWriter(null)
  }, [activeView, saveLayout])
  useEffect(() => () => useGhostStore.getState().close(), [projectId])
  const linkedFlow = useOpenLinkedProposal(projectId)
  const runLinkedFlow = useRunScenario(projectId, contractBranchId)

  // The Add Element dialog, with the parent and the kind it starts with. Null when it is closed.
  // An entry of the Create tab gives its name and tags too, such as an external system.
  const [addElementRequest, setAddElementRequest] = useState<{ parentId: string | null; kind?: string; label?: string; tags?: string[] } | null>(null)
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
    if (activeViewId || activeDocId || autoViewRef.current || linkedViewId) return
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
  }, [activeViewId, activeDocId, views, linkedViewId])

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

  const handleTidy = () => {
    if (!elements || !relationships) return
    return layoutModel(elements, relationships)
  }

  const layoutModel = async (elements: Element[], relationships: Relationship[]) => {
    // Each card gets the room it takes on the canvas, and each level takes the shape of the
    // canvas that no panel covers.
    const sized = elements.map((e) => ({
      id: e.id,
      parentId: e.parentId ?? null,
      ...cardSize(e.kind),
    }))
    const free = visibleCanvasRect()
    // The layout saves the route of each line with the places, so a line passes the cards between its ends.
    const { positions, routes } = await computeLayout(sized, relationships, free ? { width: free.width, height: free.height } : undefined, useCanvasStore.getState().expandedNodeIds, lineVisibility)
    saveLayout(positions, routes)
    // The layout moves every node, so show the result: fit once the nodes have their new places.
    requestAnimationFrame(() => requestAnimationFrame(() => fitViewRef.current?.()))
  }

  // A start of the empty project wrote to the model: read the model again and lay it out.
  const queryClient = useQueryClient()
  const [typeItOpen, setTypeItOpen] = useState(false)
  const relayoutAfterStart = async (everything: boolean) => {
    const branch = { branchId: activeDraftStreamId ?? undefined }
    await queryClient.invalidateQueries({ queryKey: everything ? ['projects', projectId] : ['projects', projectId, 'elements'] })
    if (!everything) await queryClient.invalidateQueries({ queryKey: ['projects', projectId, 'relationships'] })
    const fresh = queryClient.getQueryData<Element[]>(['projects', projectId, 'elements', branch]) ?? []
    const freshRelationships = queryClient.getQueryData<Relationship[]>(['projects', projectId, 'relationships', branch]) ?? []
    if (fresh.length > 0) await layoutModel(fresh, freshRelationships)
  }
  const handleDescribe = async (prose: string) => {
    const result = await designApi.proposeArchitecture(projectId, prose, contractBranchId)
    if (isUnavailable(result)) throw new Error(result.reason.message)
    useGhostStore.getState().open(projectId, result)
  }
  // An empty main model offers the starts instead of a blank canvas. A draft or a revision
  // that is empty shows the canvas: its starts would write to main.
  // An open ghost layer shows on the canvas, so a described system is seen before it is accepted.
  const ghostOpen = useGhostStore((s) => s.proposal !== null && s.projectId === projectId)
  const showStarts = !readOnly && !isReviewMode && !ghostOpen && selection.kind === 'main' && (elements?.length ?? 0) === 0

  // A completed scan applied what it observed: the model and "Needs you" are read again.
  useScanCompletion(projectId)
  const needsYou = useNeedsYouByEntity(projectId)
  // A card shows "needs you" for its own fact and for a contract it owns.
  const needsYouCards = useMemo(() => {
    const ids = new Set<string>()
    // A fact not seen in the latest scan has its own badge; the removal item adds none.
    for (const [id, items] of needsYou) {
      if (items.some((i) => i.proposal.action !== 'remove')) ids.add(reviewOwners[id] ?? id)
    }
    return ids
  }, [needsYou, reviewOwners])

  // An imported element without a place on the open view gets one from the auto layout, so the
  // canvas shows a diagram and not a pile. Nothing a person placed moves: a new card lands in free
  // space beside the cards and the routed lines of its level, and an open parent grows to hold it.
  // The first import of a view lays out the whole view with its routes, opens its top level (the
  // Context view) and fits it.
  const placing = useRef<string | null>(null)
  useEffect(() => {
    if (readOnly || activeDraftStreamId || isReviewMode || !activeView || !elements || !relationships) return
    const placed = new Set(activeView.positions.map((p) => p.elementId))
    const imported = elements.filter((e) => !placed.has(e.id) && e.provenance?.revision && e.factStatus !== 'inferred')
    if (imported.length === 0) return
    const key = `${activeView.id}:${imported.map((e) => e.id).sort().join(',')}`
    if (placing.current === key) return
    placing.current = key
    const firstImport = activeView.positions.length === 0
    const free = visibleCanvasRect()
    const sized = elements
      .filter((e) => placed.has(e.id) || imported.some((i) => i.id === e.id))
      .map((e) => ({ id: e.id, parentId: e.parentId ?? null, ...cardSize(e.kind) }))
    void placeNew(sized, relationships, activeView.positions, free ? { width: free.width, height: free.height } : undefined, useCanvasStore.getState().expandedNodeIds, savedRoutesOf(activeView, relationships), lineVisibilityOf(activeView.lineVisibility))
      .then(({ positions: fresh, routes }) => {
        if (fresh.length === 0) return
        // A parent that grows comes back with its new size, in place of its saved row.
        const changed = new Set(fresh.map((p) => p.elementId))
        writeLayout([...activeView.positions.filter((p) => !changed.has(p.elementId)), ...fresh], routes ?? undefined)
        if (firstImport) {
          // A repository is often one system: its top level would be a single card, so the
          // first import opens that system's inside, where its apps and stores are.
          const top = elements.filter((e) => !e.parentId)
          const only = top.length === 1 && elements.some((e) => e.parentId === top[0].id) ? top[0] : null
          if (only) useCanvasStore.getState().showInside([{ elementId: only.id, name: only.name, kind: only.kind }])
          else useCanvasStore.getState().drillTo(-1)
          requestAnimationFrame(() => requestAnimationFrame(() => fitViewRef.current?.()))
        }
      })
  }, [readOnly, activeDraftStreamId, isReviewMode, activeView, elements, relationships, writeLayout])

  // The parent is a fact of the model: the command carries it. Showing the parent's inside
  // is view state only, so the new child is on the canvas when the command returns.
  const handleCreateElement = (name: string, kind: string, parentId: string | null, tags?: string[]) => {
    const id = crypto.randomUUID()
    useUndoStore.getState().execute({
      label: `Add ${kind}`,
      execute: () => addElement.mutate({ id, kind, name, parentId: parentId ?? undefined, tags }),
      undo: () => removeElement.mutate(id),
    })
    if (parentId) {
      useCanvasStore.getState().showInside(
        pathTo(elements ?? [], parentId).map((e) => ({ elementId: e.id, name: e.name, kind: e.kind })),
      )
    }
  }

  // Without a parent the dialog starts on the level the canvas shows.
  const handleOpenCreateDialog = (parentId?: string | null, kind?: string, entry?: { label: string; tags: string[] }) => {
    setAddElementRequest({ parentId: parentId === undefined ? useCanvasStore.getState().currentRootId : parentId, kind, ...entry })
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

  // A kind dragged from the Create tab and dropped on the canvas. The canvas chose the parent by
  // the containment rules; the place is relative to the parent's card when it has one. Dropped on
  // a closed card, the element takes free space inside it, as an imported element does, and the
  // card grows to hold it.
  const handleDropElement = (entry: { kind: string; name: string; tags: string[] }, parentId: string | null, position: { x: number; y: number } | null) => {
    const id = crypto.randomUUID()
    const view = activeView
    useUndoStore.getState().execute({
      label: `Add ${entry.name.toLowerCase()}`,
      execute: () => {
        addElement.mutate({ id, kind: entry.kind, name: `New ${entry.name.toLowerCase()}`, parentId: parentId ?? undefined, tags: entry.tags })
        const positions = view?.positions ?? []
        if (position) {
          saveLayout([...positions, { elementId: id, x: position.x, y: position.y }])
          return
        }
        if (!view || !elements || !relationships) return
        const placed = new Set(positions.map((p) => p.elementId))
        const sized = [
          ...elements.filter((e) => placed.has(e.id)).map((e) => ({ id: e.id, parentId: e.parentId ?? null, ...cardSize(e.kind) })),
          { id, parentId, ...cardSize(entry.kind) },
        ]
        const free = visibleCanvasRect()
        const expanded = new Set(useCanvasStore.getState().expandedNodeIds)
        if (parentId) expanded.add(parentId)
        void placeNew(sized, relationships, positions, free ? { width: free.width, height: free.height } : undefined, expanded, savedRoutesOf(view, relationships), lineVisibilityOf(view.lineVisibility))
          .then(({ positions: fresh }) => {
            const changed = new Set(fresh.map((p) => p.elementId))
            saveLayout([...positions.filter((p) => !changed.has(p.elementId)), ...fresh])
          })
      },
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

  const handleAddElementAtFlowPosition = (kind: string, position: { x: number; y: number }, parentId?: string | null) => {
    const currentRootId = parentId === undefined ? useCanvasStore.getState().currentRootId : parentId
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

  // Hiding a card is view data: the element stays in the model and in every other view.
  const hiddenElementIds = useMemo(() => activeView?.hiddenElementIds ?? [], [activeView])
  const handleHideCards = (ids: string[]) => {
    const before = hiddenElementIds
    const after = [...new Set([...before, ...ids])]
    if (after.length === before.length) return
    const shell = useShellStore.getState()
    if (shell.selectedElementId && ids.includes(shell.selectedElementId)) shell.selectElement(null)
    useUndoStore.getState().execute({
      label: ids.length === 1 ? 'Hide a card' : `Hide ${ids.length} cards`,
      execute: () => setHiddenCards.mutate(after),
      undo: () => setHiddenCards.mutate(before),
    })
  }
  const handleShowCards = (ids: string[]) => {
    const before = hiddenElementIds
    const shown = new Set(ids)
    const after = before.filter((id) => !shown.has(id))
    if (after.length === before.length) return
    useUndoStore.getState().execute({
      label: ids.length === 1 ? 'Show a hidden card' : `Show ${ids.length} hidden cards`,
      execute: () => setHiddenCards.mutate(after),
      undo: () => setHiddenCards.mutate(before),
    })
  }

  // Align, distribute and tidy of a selection: one step to undo. A card without a saved place
  // before has none again after an undo.
  const handleMoveCards = (moves: { elementId: string; x: number; y: number }[], label: string) => {
    const saved = activeView?.positions ?? []
    const moved = new Set(moves.map((m) => m.elementId))
    const old = saved.filter((p) => moved.has(p.elementId))
    const write = (places: { elementId: string; x: number; y: number }[], drop: Set<string>) => {
      const current = (queryClient.getQueryData<typeof activeView>(['projects', projectId, 'views', activeViewId]) ?? activeView)?.positions ?? []
      const byId = new Map(places.map((p) => [p.elementId, p]))
      const kept = current.filter((p) => !drop.has(p.elementId)).map((p) => (byId.has(p.elementId) ? { ...p, x: byId.get(p.elementId)!.x, y: byId.get(p.elementId)!.y } : p))
      const added = places.filter((p) => !current.some((c) => c.elementId === p.elementId))
      saveLayout([...kept, ...added])
    }
    useUndoStore.getState().execute({
      label,
      execute: () => write(moves, new Set()),
      undo: () => write(old, new Set([...moved].filter((id) => !old.some((p) => p.elementId === id)))),
    })
  }

  // Several writes that depend on each other, one after the other: a parent before its child,
  // an element before its relationships.
  const inOrder = async (steps: (() => Promise<unknown>)[]) => {
    try {
      for (const step of steps) await step()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : 'A change was not saved.')
    }
  }

  // The delete of two or more selected elements asks for the count to be typed first.
  const [deleteRequest, setDeleteRequest] = useState<string[] | null>(null)
  const handleConfirmDeleteSelection = (ids: string[]) => {
    const all = elements ?? []
    const byId = new Map(all.map((e) => [e.id, e]))
    const chosen = new Set(ids)
    const inChosen = (id: string) => {
      for (let at = byId.get(id)?.parentId; at; at = byId.get(at)?.parentId) if (chosen.has(at)) return true
      return false
    }
    const roots = ids.filter((id) => byId.has(id) && !inChosen(id))
    // What goes with them, parents first, so an undo adds a parent before its children.
    const gone: Element[] = []
    const queue = roots.map((id) => byId.get(id)!)
    while (queue.length > 0) {
      const el = queue.shift()!
      gone.push(el)
      queue.push(...all.filter((e) => e.parentId === el.id))
    }
    const goneIds = new Set(gone.map((e) => e.id))
    const lines = (relationships ?? []).filter((r) => goneIds.has(r.fromId) || goneIds.has(r.toId))
    if (readOnly) return refuseWrite()
    useUndoStore.getState().execute({
      label: `Delete ${roots.length} elements`,
      execute: () => void inOrder(roots.map((id) => () => removeElement.mutateAsync(id))),
      undo: () => void inOrder([
        ...gone.map((el) => () => addElement.mutateAsync({
          id: el.id, kind: el.kind, name: el.name, description: el.description ?? undefined, technology: el.technology ?? undefined,
          parentId: el.parentId ?? undefined, status: el.status, tags: el.tags,
        })),
        ...lines.map((r) => () => addRelationship.mutateAsync({ id: r.id, fromId: r.fromId, toId: r.toId, label: r.label ?? undefined, technology: r.technology ?? undefined })),
      ]),
    })
  }

  // A relationship from one element to one the person picks from the model.
  const [relationshipFrom, setRelationshipFrom] = useState<string | null>(null)

  // A paste adds new elements from the copied cards, with what is inside them and the
  // relationships between them, at the pointer.
  const handlePaste = (parentId: string | null, at: { x: number; y: number }) => {
    const clip = useClipboardStore.getState().clip
    if (!clip || readOnly) return
    const parentKind = parentId ? elements?.find((e) => e.id === parentId)?.kind ?? null : null
    const kinds = validKindsForContext(parentKind)
    const set = pasteSet(clip, parentId, at, (kind) => kinds.includes(kind))
    if (set.elements.length === 0) return
    const newIds = set.elements.map((e) => e.newId)
    const newLines = set.relationships.map((r) => r.newId)
    useUndoStore.getState().execute({
      label: `Paste ${set.elements.length} element${set.elements.length === 1 ? '' : 's'}`,
      execute: () => {
        const current = (queryClient.getQueryData<typeof activeView>(['projects', projectId, 'views', activeViewId]) ?? activeView)?.positions ?? []
        saveLayout([...current.filter((p) => !newIds.includes(p.elementId)), ...set.positions])
        void inOrder([
          ...set.elements.map((el) => () => addElement.mutateAsync({
            id: el.newId, kind: el.kind, name: el.name, description: el.description ?? undefined, technology: el.technology ?? undefined,
            parentId: el.parentId ?? undefined, status: el.status, tags: el.tags,
          })),
          ...set.relationships.map((r) => () => addRelationship.mutateAsync({ id: r.newId, fromId: r.fromId, toId: r.toId, label: r.label ?? undefined, technology: r.technology ?? undefined })),
        ])
      },
      undo: () => void inOrder([
        ...newLines.map((id) => () => removeRelationship.mutateAsync(id)),
        // Removing a pasted card removes what is inside it.
        ...set.elements.filter((e) => !newIds.includes(e.parentId ?? '')).map((e) => () => removeElement.mutateAsync(e.newId)),
      ]),
    })
  }

  const viewActions = activeView
    ? {
        onExport: (format: ViewExport) => {
          const job = format === 'dsl'
            ? downloadStructurizr(projectId, projectName, openRevision?.id)
            : downloadView(projectId, activeView.id, activeView.name, format, openRevision?.id)
          job.catch((error: unknown) => toast.error(error instanceof Error ? error.message : 'The export did not complete.'))
        },
        onCopyLink: () => {
          const url = `${window.location.origin}/projects/${projectId}?view=${encodeURIComponent(activeView.id)}`
          navigator.clipboard.writeText(url).then(
            () => toast.success(`Copied a link to ${activeView.name}. Members of the workspace can open it.`),
            () => toast.error('The browser did not allow the copy.'),
          )
        },
      }
    : undefined

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

  // The template text goes with the create: one request, so the document never exists
  // without its text.
  const handleCreateDoc = useCallback((title: string, content: string, type?: string) => {
    const id = crypto.randomUUID()
    const draftId = useDraftStore.getState().activeDraftId
    createDoc.mutate(
      { id, title, draftId: draftId ?? undefined, type, content: content || undefined },
      { onSuccess: () => useShellStore.getState().openDoc(id) },
    )
  }, [createDoc])

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
  const canShare = project?.yourRole === 'Editor' || project?.yourRole === 'Owner'
  const hasElements = (elements?.length ?? 0) > 0
  const access = useMemo<AreaAccess>(() => ({ hasElements, canShare }), [hasElements, canShare])

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
        <LeftToolbar access={access}>
          <div className="flex h-full flex-col">
          {/* Says which model the open panel reads when the workbench shows another model than main. */}
          <ScopeNote flyout={activeFlyout} />
          <div className="min-h-0 flex-1">
          {activeFlyout === 'files' && (
            <FilesFlyout
              projectId={projectId}
              views={views ?? []}
              docs={docs ?? []}
              spaces={spaces ?? []}
              elements={elements ?? []}
              canEdit={!!project && canEditModel(project.yourRole)}
              model={project && { projectId, projectName: project.name, canImport: canEditModel(project.yourRole) }}
              revision={openRevision}
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
              decisions={hasElements}
              onCreateDiagram={handleCreateDiagram}
              onCreateDoc={handleCreateDoc}
              onAddElement={(entry) => handleOpenCreateDialog(undefined, entry.kind, { label: entry.name, tags: entry.tags })}
            />
          )}
          {activeFlyout === 'contracts' && (
            <ContractsFlyout projectId={projectId} branchId={contractBranchId} revision={openRevision} elements={elements ?? []} />
          )}
          {activeFlyout === 'scenarios' && (
            <ScenariosFlyout projectId={projectId} branchId={contractBranchId} references={scenarioReferences} revision={openRevision} />
          )}
          {activeFlyout === 'import' && <ImportFlyout projectId={projectId} owners={reviewOwners} labels={referenceLabels} readOnly={readOnly} />}
          {activeFlyout === 'promises' && <PromisesFlyout projectId={projectId} references={scenarioReferences} scenarios={scenarioList ?? []} owners={reviewOwners} />}
          {activeFlyout === 'review' && (
            <ReviewFlyout projectId={projectId} branchId={contractBranchId} revision={openRevision} references={scenarioReferences} elementIds={reviewElementIds} owners={reviewOwners} />
          )}
          {activeFlyout === 'build' && <BuildFlyout projectId={projectId} elements={elements ?? []} selection={() => selectedNodeIdsRef.current} />}
          {activeFlyout === 'share' && project && <ShareLinksPanel projectId={projectId} workspaceId={project.workspaceId} />}
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
          </div>
          </div>
        </LeftToolbar>
        <main className="relative flex-1">
          {activeDocId ? (
            <DocEditor projectId={projectId} docId={activeDocId} />
          ) : showStarts ? (
            <EmptyProject
              projectId={projectId}
              onTypeIt={() => setTypeItOpen(true)}
              onDescribe={handleDescribe}
              onSourceStarted={() => useShellStore.getState().openFlyout('import')}
              onTemplateOpened={() => relayoutAfterStart(true)}
              onAddSystem={() => handleOpenCreateDialog()}
            />
          ) : (
            <CanvasView
              elements={(elements ?? []).map((e) => ({ ...e, ownerId: e.ownerId ?? null, needsYou: needsYouCards.has(e.id) }))}
              relationships={relationships ?? []}
              positions={positions}
              diffStateMap={diffStateMap}
              isReviewMode={isReviewMode}
              readOnly={readOnly}
              messages={messages ?? []}
              onFitViewReady={handleFitViewReady}
              onFactLabel={(entityType, entityId, action) => factLabel.mutate({ entityType, entityId, action }, {
                onError: (error) => toast.error(error.message),
              })}
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
              onTidy={handleTidy}
              onRenameElement={handleRenameElement}
              onConnectToBlank={handleConnectToBlank}
              onAddElementAtFlowPosition={handleAddElementAtFlowPosition}
              onDropElement={handleDropElement}
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
              commentMarkers={commentMarkers}
              edgeSides={activeView?.edges}
              lineRoutes={activeView?.lines}
              onEdgeSidesChange={activeView ? handleEdgeSidesChange : undefined}
              hiddenElementIds={hiddenElementIds}
              onHideCards={activeView ? handleHideCards : undefined}
              onShowCards={activeView ? handleShowCards : undefined}
              onMoveCards={activeView ? handleMoveCards : undefined}
              onDeleteSelection={setDeleteRequest}
              onAddRelationshipFrom={setRelationshipFrom}
              onPaste={activeView ? handlePaste : undefined}
              viewActions={viewActions}
              lineControls={!!activeView}
              lineVisibility={lineVisibility}
              onLineVisibilityChange={activeView && !readOnly ? handleLineVisibilityChange : undefined}
            />
          )}
          {!activeDocId && <AssistantCanvasButton />}
          {typeItOpen && !activeDocId && !readOnly && (
            <TypeItPanel
              projectId={projectId}
              branchId={activeDraftStreamId}
              defaultSystem={projectName}
              elements={elements ?? []}
              relationships={relationships ?? []}
              onWritten={() => relayoutAfterStart(false)}
              onClose={() => setTypeItOpen(false)}
              // The panel covers the bottom of the canvas; the fit keeps the cards clear of it.
              onRoomChange={() => { if (!showStarts) fitViewRef.current?.() }}
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
          edgeSides={activeView?.edges}
          onUpdateEdgeSides={activeView ? handleEdgeSidesChange : undefined}
          labels={referenceLabels}
        />
      </div>
      {linkedFlow.flow && (
        <FlowProposalDialog
          projectId={projectId}
          proposal={linkedFlow.flow}
          labels={referenceLabels}
          onClose={linkedFlow.close}
          onAccepted={(scenarioId) => {
            linkedFlow.close()
            runLinkedFlow.mutate(scenarioId, { onSuccess: (run) => useTraceStore.getState().load(run, scenarioId) })
          }}
        />
      )}
      {!activeDocId && <TraceDock />}
      <StressDock projectId={projectId} hidden={!!activeDocId} />
      {activeDocId ? null : (
        isReviewMode ? (
          <ReviewBar projectId={projectId} />
        ) : (
          <StatusBar onTidy={handleTidy} onZoomToFit={() => fitViewRef.current?.()} />
        )
      )}
      <CommandPalette
        elements={elements ?? []}
        access={access}
        onAddElement={() => handleOpenCreateDialog()}
        onTidy={handleTidy}
        onZoomToFit={() => fitViewRef.current?.()}
      />

      {deleteRequest && (() => {
        const chosen = deleteRequest.map((id) => elements?.find((e) => e.id === id)).filter((e): e is Element => !!e)
        const inside = (id: string): number => (elements ?? []).filter((e) => e.parentId === id).reduce((n, e) => n + 1 + inside(e.id), 0)
        return (
          <TypedDeleteDialog
            noun="elements"
            name={String(chosen.length)}
            title={`Delete ${chosen.length} elements`}
            description="These leave the model, with every element inside them and every relationship to them:"
            items={chosen.map((e) => {
              const n = inside(e.id)
              return n > 0 ? `${e.name} and ${n} ${n === 1 ? 'element' : 'elements'} inside` : e.name
            })}
            note="Undo puts the elements and their relationships back. A message they produce or consume does not come back."
            onConfirm={() => {
              handleConfirmDeleteSelection(chosen.map((e) => e.id))
              setDeleteRequest(null)
            }}
            onClose={() => setDeleteRequest(null)}
          />
        )
      })()}

      {relationshipFrom && (() => {
        const from = elements?.find((e) => e.id === relationshipFrom)
        if (!from) return null
        const nameOf = (id: string | null) => (id ? elements?.find((e) => e.id === id)?.name : undefined)
        return (
          <PickDialog
            title={`Add a relationship from ${from.name}`}
            options={(elements ?? [])
              .filter((e) => e.id !== from.id)
              .sort((a, b) => a.name.localeCompare(b.name))
              .map((e) => ({ value: e.id, label: nameOf(e.parentId ?? null) ? `${e.name} · in ${nameOf(e.parentId ?? null)}` : e.name, hint: e.kind }))}
            current={null}
            submitLabel="Add relationship"
            onSubmit={(to) => {
              if (to) handleConnect(from.id, to)
              setRelationshipFrom(null)
            }}
            onClose={() => setRelationshipFrom(null)}
          />
        )
      })()}

      {addElementRequest && (
        <AddElementDialog
          parentId={addElementRequest.parentId}
          kind={addElementRequest.kind}
          label={addElementRequest.label}
          elements={elements ?? []}
          pending={addElement.isPending}
          onClose={() => setAddElementRequest(null)}
          onAdd={(name, kind, parentId) => {
            // The tags of the Create tab entry belong to its kind, such as `external` to a system.
            handleCreateElement(name, kind, parentId, kind === addElementRequest.kind ? addElementRequest.tags : undefined)
            setAddElementRequest(null)
          }}
        />
      )}
    </div>
  )
}

/** The saved routes of a view's lines, each with the element its line starts at, for `placeNew`. */
function savedRoutesOf(view: ViewDetail, relationships: Relationship[]) {
  const fromOf = new Map(relationships.map((r) => [r.id, r.fromId]))
  return [
    ...view.edges.map((e) => ({ fromId: fromOf.get(e.relationshipId), route: parseRoute(e.routePoints) })),
    ...(view.lines ?? []).map((l) => ({ fromId: l.fromId, route: parseRoute(l.route) })),
  ].filter((r): r is { fromId: string; route: LayoutRoute } => !!r.fromId && r.route !== null)
}

/**
 * Opens the proposal that the address names, from the inbox: additions open as the ghost layer
 * on the canvas, a flow opens in its dialog. The address loses the parameter after.
 */
function useOpenLinkedProposal(projectId: string) {
  const { proposal: proposalId, flowProposal: flowId } = Route.useSearch()
  const navigate = useNavigate()
  const [flow, setFlow] = useState<FlowProposal | null>(null)

  useEffect(() => {
    if (!proposalId && !flowId) return
    let current = true
    const done = () => navigate({ to: '/projects/$projectId', params: { projectId }, search: {}, replace: true })
    const failed = () => {
      if (!current) return
      toast.error('The proposal is not on this project any more.')
      done()
    }
    if (proposalId) {
      designApi.getArchitecture(projectId, proposalId).then((proposal) => {
        if (!current) return
        if (!proposal.items.some((item) => item.status === 'pending')) toast.info('Every item of this proposal is decided. Review, Assistant, History holds the record.')
        else useGhostStore.getState().open(projectId, proposal)
        done()
      }, failed)
    } else if (flowId) {
      designApi.getFlow(projectId, flowId).then((proposal) => {
        if (!current) return
        setFlow(proposal)
        done()
      }, failed)
    }
    return () => { current = false }
  }, [proposalId, flowId, projectId, navigate])

  return { flow, close: () => setFlow(null) }
}

/**
 * Opens the thread that the address names (`?thread=`, from the inbox or a webhook link). It
 * selects the draft of the thread first. A thread on an element, a relationship or a message
 * opens in the panel of its target; any other thread opens in the Comments tab of the bell panel. A thread
 * that a merge moved to main is looked for there. The address loses the parameters after.
 */
function useOpenLinkedThread(
  projectId: string,
  threads: { id: string; status: string; target: { kind: string; id: string } }[] | undefined,
  threadsLoaded: boolean,
  elements: { id: string }[] | undefined,
  relationships: { id: string }[] | undefined,
  messages: { id: string }[] | undefined,
) {
  const { thread: threadId, draft: draftId } = Route.useSearch()
  const navigate = useNavigate()
  const selection = useDraftStore((s) => s.selection)
  const step = useRef<'draft' | 'find' | 'main' | null>(null)

  useEffect(() => {
    step.current = threadId ? (draftId ? 'draft' : 'find') : null
  }, [threadId, draftId])

  useEffect(() => {
    if (!threadId || step.current === null) return
    const done = () => {
      step.current = null
      navigate({ to: '/projects/$projectId', params: { projectId }, search: {}, replace: true })
    }

    if (step.current === 'draft' && selection.kind === 'draft' && selection.id === draftId) step.current = 'find'
    if (step.current === 'draft') {
      step.current = null
      branchesApi.drafts(projectId).then((drafts) => {
        const draft = drafts.find((d) => d.id === draftId)
        // A merged draft is closed: its open threads are on main now.
        useDraftStore.getState().select(draft ? { kind: 'draft', id: draft.id, streamId: draft.streamId, name: draft.name } : { kind: 'main' })
        step.current = draft ? 'find' : 'main'
      }, () => { step.current = 'main' })
      return
    }

    if (!threadsLoaded) return
    const found = threads?.find((t) => t.id === threadId)
    if (!found) {
      if (selection.kind !== 'main' && step.current === 'find') {
        step.current = 'main'
        useDraftStore.getState().select({ kind: 'main' })
        return
      }
      if (step.current === 'main' && selection.kind !== 'main') return
      toast.error('The comment thread is not on this project any more.')
      done()
      return
    }

    const shell = useShellStore.getState()
    const { kind, id } = found.target
    const onCanvas = found.status !== 'detached' && (
      (kind === 'element' && elements?.some((e) => e.id === id))
      || (kind === 'relationship' && relationships?.some((r) => r.id === id))
      || (kind === 'message' && messages?.some((m) => m.id === id)))
    if (kind === 'document' && found.status !== 'detached') {
      shell.openDoc(id)
    } else if (onCanvas) {
      if (kind === 'element') shell.selectElement(id)
      else if (kind === 'relationship') shell.selectRelationship(id)
      else shell.selectMessage(id)
    } else {
      shell.openComments()
    }
    shell.focusThread(found.id)
    done()
  }, [threadId, draftId, threads, threadsLoaded, selection, elements, relationships, messages, navigate, projectId])
}
