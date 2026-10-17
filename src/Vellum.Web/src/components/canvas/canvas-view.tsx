import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ReactFlow,
  ReactFlowProvider,
  MiniMap,
  Panel,
  Background,
  BackgroundVariant,
  type Node,
  type Edge,
  type NodeMouseHandler,
  type OnConnect,
  type OnConnectEnd,
  type Connection,
  type HandleType,
  ConnectionMode,
  MarkerType,
  useReactFlow,
  SelectionMode,
  useNodesState,
  useEdgesState,
  useStore,
  type FitViewOptions,
} from '@xyflow/react'
import { SearchOverlay } from './search-overlay'
import { CommentMarkers, type CommentMarker } from './comment-markers'
import { labelAction } from '@/lib/fact-labels'
import { NodeContextMenu } from './context-menus/node-context-menu'
import { EdgeContextMenu } from './context-menus/edge-context-menu'
import { CanvasContextMenu } from './context-menus/canvas-context-menu'
import { CreatePopover } from './create-popover'
import '@xyflow/react/dist/style.css'
import { nodeTypes } from './nodes/node-types'
import { edgeTypes } from './edges/edge-types'
import { BulkActionsToolbar } from './bulk-actions-toolbar'
import { TruthLegend } from './truth-legend'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { useLod } from '@/hooks/use-lod'
import { Button } from '@/components/ui/button'
import type { C4ElementData } from './nodes/c4-element-node'
import type { C4ContainerData } from './nodes/c4-container-node'
import type { C4BoundaryData } from './nodes/c4-boundary-node'
import { canvasOcclusion } from '@/lib/canvas-insets'
import type { DiffState } from '@/stores/draft-store'
import type { Provenance, StaleMark } from '@/lib/fact-status'
import { kindColor } from '@/lib/kind-colors'
import { CONTAINMENT_RULES, canContain } from '@/lib/containment-rules'
import { prefersReducedMotion } from '@/lib/utils'
import { traceEdgeLabel, traceFamily } from '@/lib/trace'
import { currentTraceEvent, useTraceStore } from '@/stores/trace-store'
import { TraceEdgeMarkers } from './edges/trace-edge'
import { ImpactStepMarkers } from './edges/impact-step-edge'
import { CERTAINTY, strongest } from '@/lib/impact'
import type { ImpactCertainty } from '@/lib/import-api'
import { cardSide, effectiveSides, type CardSide, type Rect } from '@/lib/edge-utils'

interface ElementModel {
  id: string
  kind: string
  name: string
  description: string | null
  technology: string | null
  status: string
  tags: string[]
  ownerId: string | null
  parentId: string | null
  icon: string | null
  factStatus?: string | null
  stale?: StaleMark | null
  provenance?: Provenance | null
  /** A change of a Source to this fact, or a fact of it, waits for a person ("Needs you"). */
  needsYou?: boolean
}

interface RelationshipModel {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
  lineShape: string | null
  kind?: string | null
  factStatus?: string | null
  provenance?: Provenance | null
}

interface LayoutPosition {
  elementId: string
  x: number
  y: number
  width?: number
  height?: number
}

/** The saved sides of one relationship in the view. A null or missing end is auto. */
export interface EdgeSidesModel {
  relationshipId: string
  sourceSide?: string | null
  targetSide?: string | null
}

interface MessageModel {
  id: string
  name: string
  producerId: string
  consumerIds: string[]
  schemaId: string | null
}

interface CanvasViewProps {
  elements: ElementModel[]
  relationships: RelationshipModel[]
  positions: LayoutPosition[]
  diffStateMap?: Map<string, DiffState>
  isReviewMode?: boolean
  messages?: MessageModel[]
  onNodeDragStop?: (id: string, x: number, y: number) => void
  onNodeResize?: (id: string, width: number, height: number) => void
  onNodeDoubleClick?: (elementId: string) => void
  onFitViewReady?: (fitView: () => void) => void
  /** Accepts a fact as declared or marks it as unknown. The canvas does not change the label: the model does. */
  onFactLabel?: (entityType: 'element' | 'relationship', id: string, action: 'accept' | 'mark-unknown') => void
  onAddElement?: () => void
  /** Asks for a new element inside `parentId`. The canvas does not add it: the model does. */
  onAddChild?: (parentId: string, kind?: string) => void
  onConnect?: (source: string, target: string) => void
  onBulkDelete?: (ids: string[]) => void
  onDuplicateElement?: (id: string) => void
  onDeleteElement?: (id: string) => void
  onReverseRelationship?: (id: string) => void
  onDeleteRelationship?: (id: string) => void
  onUpdateRelationship?: (id: string, fields: Record<string, unknown>) => void
  onAddElementAtPosition?: (kind: string) => void
  onTidy?: () => void
  onRenameElement?: (id: string, newName: string) => void
  onConnectToBlank?: (kind: string, position: { x: number; y: number }, sourceId: string) => void
  onAddElementAtFlowPosition?: (kind: string, position: { x: number; y: number }) => void
  onViewportCenterReady?: (fn: () => { x: number; y: number }) => void
  onBulkStatusChange?: (ids: string[], status: string) => void
  onBulkAddTag?: (ids: string[], tag: string) => void
  onSelectionChange?: (ids: string[]) => void
  searchOpen?: boolean
  onSearchClose?: () => void
  /**
   * The model cannot be changed here: it is a named revision, or the person is a viewer.
   * The canvas still pans, zooms, selects and drills in. It does not move, connect, rename,
   * add or delete, and it opens no menu that only writes.
   */
  readOnly?: boolean
  /** The open comment threads of each element, message and relationship, drawn as badges over the canvas. */
  commentMarkers?: Record<string, CommentMarker>
  /** The saved sides of the relationships in this view. A relationship without one is auto. */
  edgeSides?: EdgeSidesModel[]
  /**
   * Saves the sides of a relationship in this view, after an end of it is dragged to another
   * side of the same card. The relationship's source and target in the model do not change.
   */
  onEdgeSidesChange?: (relationshipId: string, sides: { sourceSide: CardSide | null; targetSide: CardSide | null }) => void
}

function minimapNodeColor(node: Node): string {
  if (node.type === 'c4-boundary') return 'transparent'
  const kind = (node.data as { kind?: string }).kind
  return kind ? kindColor(kind) : 'var(--muted-foreground)'
}


// A fit never enlarges nodes past their designed size: a model with two nodes would
// otherwise fill the screen at the highest zoom.
// On a narrow screen a fit of the whole model shrinks the labels until no one can read them.
// There the fit stops at a zoom where labels stay legible, centred on the model, and the person
// pans to the rest.
const SMALL_SCREEN = '(max-width: 639px)'
const SMALL_SCREEN_MIN_FIT_ZOOM = 0.6
const isSmallScreen = () => typeof window !== 'undefined' && !!window.matchMedia?.(SMALL_SCREEN).matches
/** Space a fit keeps between the nodes and the edge of the free canvas, in screen pixels. */
const FIT_MARGIN = 56
const FIT_MARGIN_SMALL = 24
const fitInitial = (): FitViewOptions => ({
  maxZoom: 1, padding: 0.2,
  ...(isSmallScreen() ? { minZoom: SMALL_SCREEN_MIN_FIT_ZOOM } : {}),
})
// The move of the viewport is an animation of position only. With reduced motion the viewport jumps.
const fitMs = () => (prefersReducedMotion() ? 0 : 300)
/** The margin of a fit, plus the part of the canvas that an open flyout or panel covers. */
function fitPadding(pane: HTMLElement | null, margin: number): FitViewOptions['padding'] {
  if (!pane) return 0.2
  const i = canvasOcclusion(pane.getBoundingClientRect())
  return { top: `${i.top + margin}px`, right: `${i.right + margin}px`, bottom: `${i.bottom + margin}px`, left: `${i.left + margin}px` }
}
// Element kinds drawn as cards; the frame of the level and message pills are not.
const ELEMENT_NODE_TYPES = new Set(['c4-element', 'c4-label-chip', 'c4-container'])
/** Space between the frame of a level and the cards inside it; the top holds the frame's title. */
const FRAME_INSET = { x: 40, top: 64, bottom: 40 }
export function CanvasView(props: CanvasViewProps) {
  // A read-only canvas keeps the callbacks that look and gets none of the ones that write.
  const shown: CanvasViewProps = props.readOnly
    ? {
        elements: props.elements, relationships: props.relationships, positions: props.positions,
        diffStateMap: props.diffStateMap, isReviewMode: props.isReviewMode, messages: props.messages,
        onNodeDoubleClick: props.onNodeDoubleClick, onFitViewReady: props.onFitViewReady,
        onViewportCenterReady: props.onViewportCenterReady, onSelectionChange: props.onSelectionChange,
        searchOpen: props.searchOpen, onSearchClose: props.onSearchClose, readOnly: true,
        commentMarkers: props.commentMarkers, edgeSides: props.edgeSides,
      }
    : props
  return (
    <ReactFlowProvider>
      <CanvasViewInner {...shown} />
    </ReactFlowProvider>
  )
}

type ContextMenuState =
  | { type: 'node'; nodeId: string; position: { x: number; y: number } }
  | { type: 'edge'; edgeId: string; position: { x: number; y: number } }
  | { type: 'canvas'; position: { x: number; y: number } }
  | null

function CanvasViewInner({
  elements,
  relationships,
  positions,
  diffStateMap,
  isReviewMode,
  messages,
  onNodeDragStop,
  onNodeResize,
  onNodeDoubleClick,
  onFitViewReady,
  onFactLabel,
  onAddElement,
  onAddChild,
  onConnect: onConnectProp,
  onBulkDelete,
  onDuplicateElement,
  onDeleteElement,
  onReverseRelationship,
  onDeleteRelationship,
  onUpdateRelationship,
  onAddElementAtPosition,
  onTidy,
  onRenameElement,
  onConnectToBlank,
  onAddElementAtFlowPosition,
  onViewportCenterReady,
  onBulkStatusChange,
  onBulkAddTag,
  onSelectionChange,
  searchOpen,
  onSearchClose,
  readOnly = false,
  commentMarkers,
  edgeSides,
  onEdgeSidesChange,
}: CanvasViewProps) {
  const { currentRootId, zoomLevel, setZoom, activeLens, setLens, expandedNodeIds, toggleExpand } = useCanvasStore()
  const { fitView, screenToFlowPosition, getViewport, setViewport, getInternalNode } = useReactFlow()
  // The zoom is read from the viewport itself: a fit or a programmatic move changes it with no
  // move event of the pane. It is stored once the viewport has settled, so an animated fit
  // does not change the level of detail on every frame.
  const viewportZoom = useStore((s) => s.transform[2])
  useEffect(() => {
    const settled = window.setTimeout(() => setZoom(viewportZoom), 80)
    return () => window.clearTimeout(settled)
  }, [viewportZoom, setZoom])
  // Read once: the first fit is made at mount.
  const [initialFit] = useState(fitInitial)
  const paneRef = useRef<HTMLDivElement>(null)
  // A fit keeps clear of the panels that lie over the canvas, so no node sits behind them.
  const fitAll = useCallback((): FitViewOptions => {
    const small = isSmallScreen()
    return { ...fitInitial(), padding: fitPadding(paneRef.current, small ? FIT_MARGIN_SMALL : FIT_MARGIN), duration: fitMs() }
  }, [])
  const fitOne = useCallback((): FitViewOptions => ({ maxZoom: 1.25, padding: fitPadding(paneRef.current, 120), duration: fitMs() }), [])
  const [selectedNodeIds, setSelectedNodeIds] = useState<string[]>([])
  const [contextMenu, setContextMenu] = useState<ContextMenuState>(null)
  const [createPopover, setCreatePopover] = useState<{
    flowPosition: { x: number; y: number }
    screenPosition: { x: number; y: number }
    sourceNodeId: string | null
    rootId: string | null
  } | null>(null)
  const [searchMatchIds, setSearchMatchIds] = useState<string[]>([])
  const { selectElement, selectRelationship, selectMessage, focusElementId, clearFocusElement } = useShellStore()

  useEffect(() => {
    // The click on Fit can close the detail sheet. The fit waits a frame, so it measures the
    // panels that are still open after that click.
    onFitViewReady?.(() => requestAnimationFrame(() => fitView(fitAll())))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fitView, fitAll])

  useEffect(() => {
    if (focusElementId) {
      fitView({ nodes: [{ id: focusElementId }], ...fitOne() })
      clearFocusElement()
    }
  }, [focusElementId, fitView, clearFocusElement, fitOne])

  const getViewportCenter = useCallback(() => {
    const pane = document.querySelector('.react-flow__renderer')
    if (!pane) return { x: 0, y: 0 }
    const rect = pane.getBoundingClientRect()
    return screenToFlowPosition({
      x: rect.left + rect.width / 2,
      y: rect.top + rect.height / 2,
    })
  }, [screenToFlowPosition])

  useEffect(() => {
    onViewportCenterReady?.(getViewportCenter)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [getViewportCenter])
  const tier = useLod(zoomLevel)

  const visibleElements = useMemo(() => {
    if (currentRootId === null) {
      return elements.filter((e) => e.parentId === null)
    }
    return elements.filter((e) => e.parentId === currentRootId)
  }, [elements, currentRootId])

  const visibleIds = useMemo(() => {
    const ids = new Set(visibleElements.map((e) => e.id))
    // Also include children of expanded nodes so edges between them are visible
    for (const el of visibleElements) {
      if (expandedNodeIds.has(el.id)) {
        for (const child of elements) {
          if (child.parentId === el.id) {
            ids.add(child.id)
          }
        }
      }
    }
    return ids
  }, [visibleElements, elements, expandedNodeIds])

  const visibleRelationships = useMemo(() => {
    return relationships.filter(
      (r) => visibleIds.has(r.fromId) && visibleIds.has(r.toId),
    )
  }, [relationships, visibleIds])

  const sidesMap = useMemo(() => new Map((edgeSides ?? []).map((e) => [e.relationshipId, e])), [edgeSides])

  const positionMap = useMemo(() => {
    const map = new Map<string, LayoutPosition>()
    for (const p of positions) map.set(p.elementId, p)
    return map
  }, [positions])

  const nodes: Node[] = useMemo(() => {
    const result: Node[] = []

    // A system or an app offers "add inside" on its node. A review shows changes only.
    const addInside = (el: ElementModel) =>
      onAddChild && !isReviewMode && canContain(el.kind) ? () => onAddChild(el.id) : undefined

    for (const [index, el] of visibleElements.entries()) {
      const isExpanded = expandedNodeIds.has(el.id)
      const col = index % 3
      const row = Math.floor(index / 3)
      const savedPos = positionMap.get(el.id)
      const position = savedPos ? { x: savedPos.x, y: savedPos.y } : { x: col * 340, y: row * 200 }
      const entityDiffState = diffStateMap?.get(el.id) ?? (isReviewMode ? 'unchanged' : undefined)

      if (isExpanded) {
        const children = elements.filter((child) => child.parentId === el.id)
        // Without a saved size the boundary is as large as the default grid of its children,
        // so a child is not pushed onto its neighbour by the boundary's edge.
        const columns = Math.min(3, Math.max(1, children.length))
        const rows = Math.max(1, Math.ceil(children.length / 3))
        // Render as container boundary
        result.push({
          id: el.id,
          type: 'c4-container',
          position,
          data: {
            id: el.id,
            kind: el.kind,
            name: el.name,
            icon: el.icon,
            onResize: onNodeResize ? (width: number, height: number) => onNodeResize(el.id, width, height) : undefined,
            onAddChild: addInside(el),
          } satisfies C4ContainerData,
          style: {
            width: savedPos?.width ?? 40 + columns * 300 + 20,
            height: savedPos?.height ?? 60 + rows * 180 + 20,
          },
        })

        // Add children inside the container
        children.forEach((child, ci) => {
          const savedChildPos = positionMap.get(child.id)
          const childPos = savedChildPos ? { x: savedChildPos.x, y: savedChildPos.y } : { x: 40 + (ci % 3) * 300, y: 60 + Math.floor(ci / 3) * 180 }
          const childDiffState = diffStateMap?.get(child.id) ?? (isReviewMode ? 'unchanged' : undefined)
          result.push({
            id: child.id,
            type: tier === 'chip' ? 'c4-label-chip' : 'c4-element',
            position: childPos,
            parentId: el.id,
            extent: 'parent' as const,
            data: {
              id: child.id,
              kind: child.kind,
              name: child.name,
              description: child.description,
              technology: child.technology,
              status: child.status,
              tags: child.tags,
              ownerId: child.ownerId,
              icon: child.icon,
              factStatus: child.factStatus ?? null,
              stale: child.stale ?? null,
              needsYou: child.needsYou ?? false,
              provenance: child.provenance ?? null,
              lod: tier,
              diffState: childDiffState,
              childCount: elements.filter((e) => e.parentId === child.id).length,
              onAddChild: addInside(child),
              onRename: onRenameElement ? (newName: string) => onRenameElement(child.id, newName) : undefined,
            } satisfies C4ElementData,
          })
        })
      } else {
        // Render as normal element
        result.push({
          id: el.id,
          type: tier === 'chip' ? 'c4-label-chip' : 'c4-element',
          position,
          data: {
            id: el.id,
            kind: el.kind,
            name: el.name,
            description: el.description,
            technology: el.technology,
            status: el.status,
            tags: el.tags,
            ownerId: el.ownerId,
            icon: el.icon,
            factStatus: el.factStatus ?? null,
            stale: el.stale ?? null,
            needsYou: el.needsYou ?? false,
            provenance: el.provenance ?? null,
            lod: tier,
            diffState: entityDiffState,
            childCount: elements.filter((e) => e.parentId === el.id).length,
            onAddChild: addInside(el),
            onRename: onRenameElement ? (newName: string) => onRenameElement(el.id, newName) : undefined,
          } satisfies C4ElementData,
        })
      }
    }

    return result
  }, [visibleElements, elements, positionMap, tier, diffStateMap, isReviewMode, expandedNodeIds, onRenameElement, onNodeResize, onAddChild])

  const edges: Edge[] = useMemo(() => {
    // Count parallel edges between each pair of nodes (ignoring direction)
    const pairCounts = new Map<string, number>()
    const pairIndex = new Map<string, number>()
    for (const rel of visibleRelationships) {
      const pairKey = [rel.fromId, rel.toId].sort().join('|')
      pairCounts.set(pairKey, (pairCounts.get(pairKey) ?? 0) + 1)
    }

    return visibleRelationships.map((rel) => {
      const pairKey = [rel.fromId, rel.toId].sort().join('|')
      const total = pairCounts.get(pairKey) ?? 1
      const idx = pairIndex.get(pairKey) ?? 0
      pairIndex.set(pairKey, idx + 1)

      return {
        id: rel.id,
        source: rel.fromId,
        target: rel.toId,
        type: 'c4-edge',
        markerEnd: {
          type: MarkerType.ArrowClosed,
          color: rel.kind === 'behavioural' ? '#2dd4bf' : 'var(--muted-foreground)',
        },
        data: {
          label: rel.label,
          technology: rel.technology,
          lineShape: rel.lineShape,
          relationshipKind: rel.kind ?? 'structural',
          factStatus: rel.factStatus ?? null,
          diffState: diffStateMap?.get(rel.id) ?? (isReviewMode ? 'unchanged' : undefined),
          edgeIndex: idx,
          totalParallel: total,
          sourceSide: cardSide(sidesMap.get(rel.id)?.sourceSide),
          targetSide: cardSide(sidesMap.get(rel.id)?.targetSide),
        },
        // Only the selected relationship offers its ends to drag; see shownEdges.
        reconnectable: false,
      }
    })
  }, [visibleRelationships, diffStateMap, isReviewMode, sidesMap])

  const messageNodes: Node[] = useMemo(() => {
    if (activeLens !== 'messages' || !messages) return []
    return messages
      .filter((m) => visibleIds.has(m.producerId))
      .map((msg) => {
        const producerPos = positionMap.get(msg.producerId) ?? { x: 0, y: 0 }
        const firstConsumerPos =
          msg.consumerIds.length > 0
            ? positionMap.get(msg.consumerIds[0]) ?? { x: producerPos.x + 400, y: producerPos.y }
            : { x: producerPos.x + 400, y: producerPos.y }
        return {
          id: `msg-${msg.id}`,
          type: 'message-pill' as const,
          position: {
            x: (producerPos.x + firstConsumerPos.x) / 2,
            y: (producerPos.y + firstConsumerPos.y) / 2,
          },
          data: {
            id: msg.id,
            name: msg.name,
            hasSchema: msg.schemaId !== null,
          },
        }
      })
  }, [activeLens, messages, visibleIds, positionMap])

  const messageEdges: Edge[] = useMemo(() => {
    if (activeLens !== 'messages' || !messages) return []
    return messages
      .filter((m) => visibleIds.has(m.producerId))
      .flatMap((msg) => {
        const producerEdge: Edge = {
          id: `msg-edge-p-${msg.id}`,
          source: msg.producerId,
          target: `msg-${msg.id}`,
          type: 'c4-edge',
          markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--muted-foreground)' },
          data: { label: null, technology: null },
          reconnectable: false,
        }
        const consumerEdges: Edge[] = msg.consumerIds
          .filter((cid) => visibleIds.has(cid))
          .map((cid) => ({
            id: `msg-edge-c-${msg.id}-${cid}`,
            source: `msg-${msg.id}`,
            target: cid,
            type: 'c4-edge',
            markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--muted-foreground)' },
            data: { label: null, technology: null },
            reconnectable: false,
          }))
        return [producerEdge, ...consumerEdges]
      })
  }, [activeLens, messages, visibleIds])

  // The event a trace playback is on. Its parts may sit inside a collapsed parent or on
  // another level, so each end resolves to the nearest ancestor that is on the canvas.
  const traceEvent = useTraceStore(currentTraceEvent)
  const traceLabels = useTraceStore((s) => s.run?.labels)
  const traceSpeed = useTraceStore((s) => s.speed)
  const onCanvas = useMemo(() => {
    const byId = new Map(elements.map((e) => [e.id, e]))
    return (id: string | null): string | null => {
      for (let current = id ? byId.get(id) : undefined; current; current = current.parentId ? byId.get(current.parentId) : undefined) {
        if (visibleIds.has(current.id)) return current.id
      }
      return null
    }
  }, [elements, visibleIds])
  const traceOverlay = useMemo(() => {
    if (!traceEvent || !traceLabels) return null
    const from = onCanvas(traceEvent.from)
    const to = onCanvas(traceEvent.to)
    const family = traceFamily(traceEvent.kind)
    return {
      family,
      nodeIds: new Set([from, to].filter((id): id is string => id !== null)),
      edge: from && to && from !== to
        ? {
            id: `trace-${traceEvent.sequence}`,
            source: from,
            target: to,
            type: 'trace-edge',
            selectable: false,
            focusable: false,
            zIndex: 1000,
            data: { family, label: traceEdgeLabel(traceEvent, traceLabels), travelSeconds: 1.1 / traceSpeed },
          }
        : null,
    }
  }, [traceEvent, traceLabels, traceSpeed, onCanvas])

  // The view follows a playback: when a part of the current event is out of the free canvas
  // (below its edge, where the dock now is, or behind a panel), it pans by the least distance
  // that brings both parts in. It zooms out only when both parts cannot fit at this zoom.
  // Each event moves the view, also one between the same two parts as the last: the person
  // may have panned away since.
  const traceNodeKey = traceOverlay && traceEvent ? `${traceEvent.sequence}:${[...traceOverlay.nodeIds].join('|')}` : ''
  useEffect(() => {
    if (!traceNodeKey) return
    const frame = requestAnimationFrame(() => {
      const pane = paneRef.current
      if (!pane) return
      const rect = pane.getBoundingClientRect()
      const ids = traceNodeKey.slice(traceNodeKey.indexOf(':') + 1).split('|')
      let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity
      for (const id of ids) {
        const node = getInternalNode(id)
        if (!node?.measured.width || !node.measured.height) return
        const { x, y } = node.internals.positionAbsolute
        minX = Math.min(minX, x)
        minY = Math.min(minY, y)
        maxX = Math.max(maxX, x + node.measured.width)
        maxY = Math.max(maxY, y + node.measured.height)
      }
      const margin = isSmallScreen() ? FIT_MARGIN_SMALL : FIT_MARGIN
      const i = canvasOcclusion(rect)
      const free = { left: i.left + margin, top: i.top + margin, right: rect.width - i.right - margin, bottom: rect.height - i.bottom - margin }
      const { x: vx, y: vy, zoom } = getViewport()
      const screen = { left: minX * zoom + vx, top: minY * zoom + vy, right: maxX * zoom + vx, bottom: maxY * zoom + vy }
      if (screen.right - screen.left > free.right - free.left || screen.bottom - screen.top > free.bottom - free.top) {
        fitView({ nodes: ids.map((id) => ({ id })), maxZoom: zoom, padding: fitPadding(pane, margin), duration: fitMs() })
        return
      }
      const shift = (lo: number, hi: number, freeLo: number, freeHi: number) =>
        lo < freeLo ? freeLo - lo : hi > freeHi ? freeHi - hi : 0
      const dx = shift(screen.left, screen.right, free.left, free.right)
      const dy = shift(screen.top, screen.bottom, free.top, free.bottom)
      if (dx !== 0 || dy !== 0) setViewport({ x: vx + dx, y: vy + dy, zoom }, { duration: fitMs() })
    })
    return () => cancelAnimationFrame(frame)
  }, [traceNodeKey, getInternalNode, getViewport, setViewport, fitView])

  // The parts of a promise or of an affected flow, shown from the Promises and the Import
  // flyouts. A part on another level shows as its nearest ancestor here, like a trace end.
  const canvasHighlight = useShellStore((s) => s.canvasHighlight)
  const clearCanvasHighlight = useShellStore((s) => s.clearCanvasHighlight)
  const highlight = useMemo(() => {
    if (!canvasHighlight) return null
    const shown = canvasHighlight.elementIds.map(onCanvas)
    // An affected flow rings each part in the certainty of the surest step or change that reaches it.
    const nodeIds = new Set(shown.filter((id): id is string => id !== null))
    const marks = new Map<string, ImpactCertainty>()
    const mark = (node: string, certainty: ImpactCertainty) => marks.set(node, marks.has(node) ? strongest([certainty, marks.get(node)!]) : certainty)
    for (const [id, certainty] of Object.entries(canvasHighlight.marks ?? {})) {
      const node = onCanvas(id)
      if (node) mark(node, certainty)
    }
    // Each affected step from where it starts to what it reaches. Steps between the same two
    // parts share one edge, as surest of them. A publish step with no handler goes to the
    // consumers of its message.
    const pairs = new Map<string, { source: string; target: string; certainty: ImpactCertainty; labels: string[] }>()
    let undrawn = 0
    for (const step of canvasHighlight.steps ?? []) {
      const source = onCanvas(step.fromId)
      const targets = step.toIds.length > 0 ? step.toIds : (messages?.find((m) => m.id === step.messageId)?.consumerIds ?? [])
      let drawn = false
      for (const target of new Set(targets.map(onCanvas))) {
        if (!source || !target || source === target) continue
        const key = `${source}>${target}`
        const pair = pairs.get(key) ?? { source, target, certainty: step.certainty, labels: [] }
        pair.certainty = strongest([pair.certainty, step.certainty])
        pair.labels.push(`${step.order}. ${step.label ?? step.stepId}`)
        pairs.set(key, pair)
        // Both ends of a drawn step are ringed, the consumers of a message too.
        for (const end of [source, target]) {
          nodeIds.add(end)
          mark(end, step.certainty)
        }
        drawn = true
      }
      if (!drawn) undrawn++
    }
    const stepEdges: Edge[] = [...pairs.entries()].map(([key, pair]) => ({
      id: `impact-step-${key}`,
      source: pair.source,
      target: pair.target,
      type: 'impact-step-edge',
      selectable: false,
      focusable: false,
      zIndex: 999,
      data: { certainty: pair.certainty, label: [...new Set(pair.labels)].join(' · ') },
    }))
    return { nodeIds, elsewhere: shown.filter((id) => id === null).length, marks, stepEdges, undrawn }
  }, [canvasHighlight, onCanvas, messages])
  useEffect(() => {
    if (highlight && highlight.nodeIds.size > 0) fitView({ nodes: [...highlight.nodeIds].map((id) => ({ id })), ...(highlight.nodeIds.size === 1 ? fitOne() : fitAll()) })
  // Only a new highlight moves the viewport, not a change of the model under it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canvasHighlight, fitView])

  // A level is shown fitted when it opens, and again when the saved places of the view arrive:
  // the first fit of the canvas runs before them, on the default grid.
  const hasPositions = positions.length > 0
  useEffect(() => {
    let inner = 0
    const outer = requestAnimationFrame(() => { inner = requestAnimationFrame(() => fitView({ ...fitAll(), duration: 0 })) })
    return () => { cancelAnimationFrame(outer); cancelAnimationFrame(inner) }
  }, [currentRootId, hasPositions, fitView, fitAll])

  // Derive the combined node/edge arrays from domain data (same as before)
  const derivedNodes = useMemo(() => {
    const all = [...nodes, ...messageNodes]
    // A playback shows one event at a time, so it takes the rings while it runs.
    if (traceOverlay) {
      return all.map((n) =>
        traceOverlay.nodeIds.has(n.id)
          ? { ...n, className: 'vellum-trace-node', style: { ...n.style, '--vellum-trace': `var(--trace-${traceOverlay.family})` } }
          : n,
      )
    }
    if (!highlight) return all
    return all.map((n) => {
      if (!highlight.nodeIds.has(n.id)) return { ...n, className: 'vellum-dimmed-node' }
      const mark = highlight.marks.get(n.id)
      return {
        ...n,
        className: 'vellum-trace-node',
        style: { ...n.style, '--vellum-trace': mark ? CERTAINTY[mark].color : 'var(--primary)', '--vellum-ring-style': mark ? CERTAINTY[mark].line : 'solid' },
      }
    })
  }, [nodes, messageNodes, traceOverlay, highlight])
  const derivedEdges = useMemo(() => {
    const traceEdge = traceOverlay?.edge
    if (!traceEdge) return [...edges, ...messageEdges, ...(highlight?.stepEdges ?? [])]
    // A relationship between the two parts the trace event joins moves its label below the
    // path, so the label of the trace above the path does not cover it.
    const traced = (e: Edge) =>
      (e.source === traceEdge.source && e.target === traceEdge.target) || (e.source === traceEdge.target && e.target === traceEdge.source)
    return [
      ...edges.map((e) => (traced(e) ? { ...e, data: { ...e.data, labelBelow: true } } : e)),
      ...messageEdges,
      traceEdge,
    ]
  }, [edges, messageEdges, traceOverlay, highlight])

  // Local state that React Flow controls — synced from derived data,
  // but RF can update positions during drag without waiting for server round-trip
  const [rfNodes, setRfNodes, onNodesChange] = useNodesState(derivedNodes)
  const [rfEdges, setRfEdges, onEdgesChange] = useEdgesState(derivedEdges)

  // Sync derived data → local RF state when domain model changes.
  // Fingerprint serializes content (dropping functions) so reference-only changes
  // from parent re-renders don't cause the sync to overwrite mid-drag positions.
  const derivedNodeFingerprint = useMemo(
    () => JSON.stringify(derivedNodes, (_k, v) => typeof v === 'function' ? undefined : v),
    [derivedNodes],
  )
  const derivedEdgeFingerprint = useMemo(
    () => JSON.stringify(derivedEdges, (_k, v) => typeof v === 'function' ? undefined : v),
    [derivedEdges],
  )

  useEffect(() => {
    setRfNodes(derivedNodes)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [derivedNodeFingerprint, setRfNodes])

  useEffect(() => {
    // The selection is React Flow's: a change of the model or the layout keeps it.
    setRfEdges((previous) => {
      const selected = new Set((previous as Edge[]).filter((e) => e.selected).map((e) => e.id))
      return selected.size === 0 ? derivedEdges : derivedEdges.map((e) => (selected.has(e.id) ? { ...e, selected: true } : e))
    })
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [derivedEdgeFingerprint, setRfEdges])

  // The element whose inside this level shows, as a frame around its children. It follows the
  // children while they are dragged: it is drawn from the nodes on the canvas, not saved.
  const rootElement = currentRootId ? elements.find((e) => e.id === currentRootId) ?? null : null
  const frameNode = useMemo<Node | null>(() => {
    if (!rootElement) return null
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity
    for (const n of rfNodes) {
      if (n.parentId || !ELEMENT_NODE_TYPES.has(n.type ?? '')) continue
      const width = n.measured?.width ?? (n.style?.width as number | undefined) ?? 264
      const height = n.measured?.height ?? (n.style?.height as number | undefined) ?? 112
      minX = Math.min(minX, n.position.x)
      minY = Math.min(minY, n.position.y)
      maxX = Math.max(maxX, n.position.x + width)
      maxY = Math.max(maxY, n.position.y + height)
    }
    if (!Number.isFinite(minX)) return null
    const width = maxX - minX + FRAME_INSET.x * 2
    const height = maxY - minY + FRAME_INSET.top + FRAME_INSET.bottom
    return {
      id: `frame-${rootElement.id}`,
      type: 'c4-boundary',
      position: { x: minX - FRAME_INSET.x, y: minY - FRAME_INSET.top },
      width,
      height,
      // Clicks and double-clicks pass through to the canvas under it.
      style: { width, height, pointerEvents: 'none' },
      zIndex: -1,
      draggable: false,
      selectable: false,
      connectable: false,
      focusable: false,
      deletable: false,
      data: { kind: rootElement.kind, name: rootElement.name, icon: rootElement.icon, technology: rootElement.technology } satisfies C4BoundaryData,
    }
  }, [rfNodes, rootElement])
  const shownNodes = useMemo(() => (frameNode ? [frameNode, ...rfNodes] : rfNodes), [frameNode, rfNodes])

  // The selected relationship offers its two ends to drag. React Flow draws the drag anchors at
  // the handles the edge names, so the edge names the handles of the sides it is drawn on: the
  // saved side, or the facing side of the live cards for an auto end.
  const relationshipIds = useMemo(() => new Set(relationships.map((r) => r.id)), [relationships])
  const shownEdges = useMemo(() => {
    if (!onEdgeSidesChange) return rfEdges
    const box = (id: string): Rect | null => {
      const node = rfNodes.find((n) => n.id === id)
      const width = node?.measured?.width
      const height = node?.measured?.height
      if (!node || !width || !height) return null
      const parent = node.parentId ? rfNodes.find((n) => n.id === node.parentId) : undefined
      return { x: node.position.x + (parent?.position.x ?? 0), y: node.position.y + (parent?.position.y ?? 0), width, height }
    }
    return (rfEdges as Edge[]).map((edge) => {
      if (!edge.selected || !relationshipIds.has(edge.id)) return edge
      const source = box(edge.source)
      const target = box(edge.target)
      if (!source || !target || edge.source === edge.target) return edge
      const data = edge.data as { sourceSide?: string | null; targetSide?: string | null } | undefined
      const sides = effectiveSides(source, target, data)
      return { ...edge, reconnectable: true, sourceHandle: sides.source, targetHandle: sides.target }
    })
  }, [rfEdges, rfNodes, relationshipIds, onEdgeSidesChange])

  // The end of a relationship being dragged, from its start to its end. While it is set, a drop
  // is valid only on a handle of the same two cards, and a drop on the pane opens no menu.
  const reconnectingRef = useRef<{ edge: Edge; end: 'source' | 'target' } | null>(null)
  const handleReconnectStart = useCallback((_event: React.MouseEvent, edge: Edge, handleType: HandleType) => {
    // React Flow names the end that stays: the source end moves when the target stays.
    reconnectingRef.current = { edge, end: handleType === 'target' ? 'source' : 'target' }
  }, [])
  // The release of a dragged end also clicks the pane under it; that click keeps the selection.
  const reconnectEndedAtRef = useRef(0)
  const handleReconnectEnd = useCallback(() => {
    reconnectingRef.current = null
    reconnectEndedAtRef.current = performance.now()
  }, [])
  const isValidConnection = useCallback((connection: Edge | Connection) => {
    const moving = reconnectingRef.current
    // Two handles of one card would make a loop, which the canvas does not draw.
    if (!moving) return connection.source !== connection.target
    return connection.source === moving.edge.source && connection.target === moving.edge.target
  }, [])
  const handleReconnect = useCallback((oldEdge: Edge, connection: Connection) => {
    const moving = reconnectingRef.current
    // A relationship keeps its two elements: an end only moves to another side of its own card.
    if (!moving || !onEdgeSidesChange || connection.source !== oldEdge.source || connection.target !== oldEdge.target) return
    const saved = sidesMap.get(oldEdge.id)
    const sourceSide = moving.end === 'source' ? cardSide(connection.sourceHandle) : cardSide(saved?.sourceSide)
    const targetSide = moving.end === 'target' ? cardSide(connection.targetHandle) : cardSide(saved?.targetSide)
    if (sourceSide === cardSide(saved?.sourceSide) && targetSide === cardSide(saved?.targetSide)) return
    onEdgeSidesChange(oldEdge.id, { sourceSide, targetSide })
  }, [onEdgeSidesChange, sidesMap])

  const handleSearchMatchChange = useCallback(
    (matchIds: string[], _currentIndex: number) => {
      // Highlighting rewrites the nodes the overlay searches, which reports the same matches
      // again. Keep the previous array when nothing changed, or the two effects feed each other.
      setSearchMatchIds((prev) =>
        prev.length === matchIds.length && prev.every((id, i) => id === matchIds[i]) ? prev : matchIds,
      )
    },
    [],
  )

  const handleSearchFocusNode = useCallback(
    (nodeId: string) => {
      fitView({ nodes: [{ id: nodeId }], ...fitOne() })
    },
    [fitView, fitOne],
  )

  useEffect(() => {
    if (!searchOpen || searchMatchIds.length === 0) {
      // Reset opacity when search closes or no matches
      setRfNodes((nds) =>
        nds.map((n) => ({
          ...n,
          style: { ...n.style, opacity: undefined },
          className: n.className?.replace('ring-2 ring-primary', '').trim() || undefined,
        })),
      )
      return
    }
    const matchSet = new Set(searchMatchIds)
    setRfNodes((nds) =>
      nds.map((n) => ({
        ...n,
        style: {
          ...n.style,
          opacity: matchSet.has(n.id) ? 1 : 0.3,
        },
        className: matchSet.has(n.id) ? 'ring-2 ring-primary' : undefined,
      })),
    )
  }, [searchOpen, searchMatchIds, setRfNodes])

  const handleNodeClick: NodeMouseHandler = useCallback(
    (_event, node) => {
      if (node.id.startsWith('msg-')) {
        selectMessage(node.id.replace('msg-', ''))
      } else {
        selectElement(node.id)
      }
    },
    [selectElement, selectMessage],
  )

  const handleNodeDoubleClick: NodeMouseHandler = useCallback(
    (_event, node) => {
      if (!node.id.startsWith('msg-')) onNodeDoubleClick?.(node.id)
    },
    [onNodeDoubleClick],
  )

  const handleEdgeClick = useCallback(
    (_event: React.MouseEvent, edge: Edge) => selectRelationship(edge.id),
    [selectRelationship],
  )

  const handleConnect: OnConnect = useCallback(
    (params) => {
      if (params.source && params.target) {
        onConnectProp?.(params.source, params.target)
      }
    },
    [onConnectProp],
  )

  const handleConnectEnd: OnConnectEnd = useCallback(
    (event, connectionState) => {
      // Only open the popover when the connection was dropped on empty canvas (no target node).
      // The end of a relationship dropped on the pane stays where it was.
      if (readOnly || reconnectingRef.current || connectionState.toNode !== null) return
      // Must have had a source node to connect from
      const sourceNodeId = connectionState.fromHandle?.nodeId ?? null
      if (!sourceNodeId) return

      const clientX = 'clientX' in event ? event.clientX : event.changedTouches[0].clientX
      const clientY = 'clientY' in event ? event.clientY : event.changedTouches[0].clientY
      const flowPos = screenToFlowPosition({ x: clientX, y: clientY })

      setCreatePopover({
        flowPosition: flowPos,
        screenPosition: { x: clientX, y: clientY },
        sourceNodeId,
        rootId: currentRootId,
      })
    },
    [screenToFlowPosition, readOnly, currentRootId],
  )

  const handlePaneDoubleClick = useCallback(
    (event: React.MouseEvent) => {
      // Only proceed when the click target is on the pane itself, excluding all overlays
      const target = event.target as HTMLElement
      // Nodes and edges render inside the pane, so a double-click on a node (which drills in)
      // also reaches here. Only the empty pane opens the menu.
      if (readOnly || !target.closest('.react-flow__pane') || target.closest('.react-flow__node, .react-flow__edge')) return

      const flowPos = screenToFlowPosition({ x: event.clientX, y: event.clientY })
      setCreatePopover({
        flowPosition: flowPos,
        screenPosition: { x: event.clientX, y: event.clientY },
        sourceNodeId: null,
        rootId: currentRootId,
      })
    },
    [screenToFlowPosition, readOnly, currentRootId],
  )

  // The menu belongs to the level and the spot where it opened: it shows only on that level.
  useEffect(() => {
    if (!createPopover) return
    const close = (event: KeyboardEvent) => { if (event.key === 'Escape') setCreatePopover(null) }
    window.addEventListener('keydown', close)
    return () => window.removeEventListener('keydown', close)
  }, [createPopover])

  const handlePopoverSelect = useCallback(
    (kind: string) => {
      if (!createPopover) return
      if (createPopover.sourceNodeId) {
        onConnectToBlank?.(kind, createPopover.flowPosition, createPopover.sourceNodeId)
      } else {
        onAddElementAtFlowPosition?.(kind, createPopover.flowPosition)
      }
      setCreatePopover(null)
    },
    [createPopover, onConnectToBlank, onAddElementAtFlowPosition],
  )

  const handlePaneClick = useCallback(() => {
    if (performance.now() - reconnectEndedAtRef.current < 250) return
    setCreatePopover(null)
    selectElement(null)
    selectRelationship(null)
    selectMessage(null)
  }, [selectElement, selectRelationship, selectMessage])

  const handleSelectionChange = useCallback(({ nodes }: { nodes: Node[] }) => {
    const ids = nodes.map((n) => n.id).filter((id) => !id.startsWith('msg-'))
    setSelectedNodeIds(ids)
    onSelectionChange?.(ids)
  }, [onSelectionChange])

  const handleNodesDelete = useCallback(
    (deleted: Node[]) => {
      const ids = deleted.map((n) => n.id).filter((id) => !id.startsWith('msg-'))
      if (ids.length > 0) onBulkDelete?.(ids)
    },
    [onBulkDelete],
  )

  const handleEdgesDelete = useCallback(
    (deleted: Edge[]) => {
      for (const edge of deleted) {
        onDeleteRelationship?.(edge.id)
      }
    },
    [onDeleteRelationship],
  )

  const handleNodeContextMenu = useCallback(
    (event: React.MouseEvent, node: Node) => {
      event.preventDefault()
      setContextMenu({ type: 'node', nodeId: node.id, position: { x: event.clientX, y: event.clientY } })
    },
    [],
  )
  // The edge menu and the canvas menu hold actions that write and nothing else that the
  // canvas does not already offer, so a read-only canvas opens neither. The node menu
  // stays, without its actions that write: it shows the details, expands and drills in.
  const noMenu = useCallback((event: React.MouseEvent | MouseEvent) => event.preventDefault(), [])

  const handleEdgeContextMenu = useCallback(
    (event: React.MouseEvent, edge: Edge) => {
      event.preventDefault()
      setContextMenu({ type: 'edge', edgeId: edge.id, position: { x: event.clientX, y: event.clientY } })
    },
    [],
  )

  const handlePaneContextMenu = useCallback(
    (event: React.MouseEvent | MouseEvent) => {
      event.preventDefault()
      setContextMenu({ type: 'canvas', position: { x: (event as MouseEvent).clientX, y: (event as MouseEvent).clientY } })
    },
    [],
  )

  if (visibleElements.length === 0) {
    const isTopLevel = currentRootId === null
    return (
      <div className="flex h-full items-center justify-center">
        <div className="text-center">
          <p className="text-sm text-muted-foreground">
            {readOnly
              ? 'This model has no element here. It is read-only.'
              : isTopLevel
                ? 'Start with a system. Draw a connection to add the next one.'
                : 'Nothing here yet. Add an element to get started.'}
          </p>
          {!readOnly && (
            <Button
              variant="outline"
              size="sm"
              className="mt-3"
              onClick={() => onAddElement?.()}
            >
              {isTopLevel ? 'Add System' : 'Add Element'}
            </Button>
          )}
        </div>
      </div>
    )
  }

  return (
    // Wrapper div captures double-click for canvas creation; pointer-events:none not needed
    // as the div fills the parent and React Flow handles its own pointer events internally
    <div ref={paneRef} className="h-full w-full" onDoubleClick={handlePaneDoubleClick}>
      <ReactFlow
        nodes={shownNodes}
        edges={shownEdges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodeClick={handleNodeClick}
        onNodeDoubleClick={handleNodeDoubleClick}
        onNodeDragStop={onNodeDragStop ? (_event, node) => onNodeDragStop(node.id, node.position.x, node.position.y) : undefined}
        onConnect={handleConnect}
        onConnectEnd={handleConnectEnd}
        // Each side of a card has one handle that starts a relationship and takes the end of one.
        connectionMode={ConnectionMode.Loose}
        isValidConnection={isValidConnection}
        edgesReconnectable={false}
        reconnectRadius={8}
        onReconnectStart={onEdgeSidesChange ? handleReconnectStart : undefined}
        onReconnect={onEdgeSidesChange ? handleReconnect : undefined}
        onReconnectEnd={onEdgeSidesChange ? handleReconnectEnd : undefined}
        onEdgeClick={handleEdgeClick}
        onPaneClick={handlePaneClick}
        onSelectionChange={handleSelectionChange}
        onNodeContextMenu={handleNodeContextMenu}
        onEdgeContextMenu={readOnly ? noMenu : handleEdgeContextMenu}
        onPaneContextMenu={readOnly ? noMenu : handlePaneContextMenu}
        nodesDraggable={!readOnly}
        nodesConnectable={!readOnly}
        // A node that cannot be dragged has no `nopan` class, so the zoom of the pane takes its
        // double-click and stops it before the node sees it. Read-only, a double-click drills in.
        zoomOnDoubleClick={!readOnly}
        deleteKeyCode={readOnly ? null : ['Delete', 'Backspace']}
        onNodesDelete={handleNodesDelete}
        onEdgesDelete={handleEdgesDelete}
        selectionMode={SelectionMode.Partial}
        selectionKeyCode="Shift"
        multiSelectionKeyCode="Shift"
        panOnDrag
        fitView
        fitViewOptions={initialFit}
        colorMode="dark"
        onlyRenderVisibleElements
        snapToGrid
        snapGrid={[16, 16]}
        minZoom={0.1}
        maxZoom={3}
      >
        <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="var(--border)" />
        {activeLens === 'facts' && (
          <TruthLegend
            factStatuses={visibleElements.map((e) => e.factStatus ?? 'declared')}
            relationshipKinds={visibleRelationships.map((r) => r.kind ?? 'structural')}
            onClose={() => setLens('none')}
          />
        )}
        <TraceEdgeMarkers />
        <ImpactStepMarkers />
        {commentMarkers && <CommentMarkers markers={commentMarkers} />}
        {canvasHighlight && highlight && (
          <Panel position="top-center" className="max-w-[calc(100%-1.5rem)] rounded-lg border border-border bg-card/95 px-3 py-1.5 text-xs shadow-md backdrop-blur">
            <div className="flex items-center gap-3">
              <p role="status" className="min-w-0 text-muted-foreground">
                <span className="block truncate text-foreground">{canvasHighlight.title}</span>
                {highlight.nodeIds.size === 0
                  ? 'No part of it is on this level of the canvas.'
                  : `${highlight.nodeIds.size} ${highlight.nodeIds.size === 1 ? 'part is' : 'parts are'} ringed.${highlight.elsewhere > 0 ? ` ${highlight.elsewhere} more ${highlight.elsewhere === 1 ? 'is' : 'are'} not on this level.` : ''}`}
              </p>
              <button type="button" className="shrink-0 rounded px-1.5 py-0.5 text-foreground hover:bg-muted focus-visible:outline-2 focus-visible:outline-primary" onClick={clearCanvasHighlight}>
                Clear
              </button>
            </div>
            {canvasHighlight.steps && canvasHighlight.steps.length > 0 && (
              <ol aria-label="Affected steps" className="mt-1.5 max-h-36 space-y-0.5 overflow-y-auto border-t border-border pt-1.5">
                {canvasHighlight.steps.map((step) => (
                  <li key={step.stepId} className="flex items-center gap-2">
                    <span aria-hidden="true" className="h-0 w-4 shrink-0 border-t-2" style={{ borderColor: CERTAINTY[step.certainty].color, borderTopStyle: CERTAINTY[step.certainty].line }} />
                    <span className="font-mono text-[11px] text-muted-foreground">{step.order}.</span>
                    <span className="min-w-0 truncate text-foreground">{step.label ?? step.stepId}</span>
                    <span className="ml-auto shrink-0 pl-2" style={{ color: CERTAINTY[step.certainty].color }}>{CERTAINTY[step.certainty].label}</span>
                  </li>
                ))}
                {highlight.undrawn > 0 && (
                  <li className="text-muted-foreground">
                    {highlight.undrawn} {highlight.undrawn === 1 ? 'step has' : 'steps have'} no second part on this level, so {highlight.undrawn === 1 ? 'it is' : 'they are'} not drawn.
                  </li>
                )}
              </ol>
            )}
          </Panel>
        )}
        <MiniMap
          nodeColor={minimapNodeColor}
          nodeStrokeColor="var(--border)"
          nodeBorderRadius={4}
          ariaLabel="Canvas minimap"
          pannable
          zoomable
          bgColor="var(--card)"
          maskColor="color-mix(in srgb, var(--background) 80%, transparent)"
          className="border-border! max-sm:hidden"
        />
        {!readOnly && <BulkActionsToolbar
          selectedCount={selectedNodeIds.length}
          onDelete={() => onBulkDelete?.(selectedNodeIds)}
          onBulkStatusChange={onBulkStatusChange ? (status) => onBulkStatusChange(selectedNodeIds, status) : undefined}
          onBulkAddTag={onBulkAddTag ? (tag) => onBulkAddTag(selectedNodeIds, tag) : undefined}
        />}
        {searchOpen && (
          <SearchOverlay
            nodes={rfNodes}
            onMatchChange={handleSearchMatchChange}
            onClose={() => onSearchClose?.()}
            onFocusNode={handleSearchFocusNode}
          />
        )}
      </ReactFlow>

      {contextMenu?.type === 'node' && (() => {
        const nodeId = contextMenu.nodeId
        const el = elements.find((e) => e.id === nodeId)
        if (!el) return null
        const children = elements.filter((e) => e.parentId === nodeId)
        return (
          <NodeContextMenu
            open
            position={contextMenu.position}
            hasChildren={children.length > 0}
            isExpanded={expandedNodeIds.has(nodeId)}
            childKinds={onAddChild && !isReviewMode ? CONTAINMENT_RULES[el.kind] ?? [] : []}
            canExpand={(el.parentId ?? null) === currentRootId}
            onAddChild={(kind) => onAddChild?.(nodeId, kind)}
            onClose={() => setContextMenu(null)}
            onEditDetails={() => selectElement(nodeId)}
            onExpand={() => toggleExpand(nodeId)}
            onDrillInto={() => {
              const { drillInto } = useCanvasStore.getState()
              drillInto({ elementId: el.id, name: el.name, kind: el.kind })
            }}
            readOnly={readOnly}
            onDuplicate={() => onDuplicateElement?.(nodeId)}
            onDelete={() => onDeleteElement?.(nodeId)}
            labelAction={isReviewMode ? null : labelAction(el.factStatus)}
            onLabelAction={onFactLabel && (() => { const action = labelAction(el.factStatus); if (action) onFactLabel('element', nodeId, action) })}
          />
        )
      })()}

      {contextMenu?.type === 'edge' && (
        <EdgeContextMenu
          open
          position={contextMenu.position}
          currentLineShape={relationships.find((r) => r.id === contextMenu.edgeId)?.lineShape}
          onClose={() => setContextMenu(null)}
          onEditDetails={() => selectRelationship(contextMenu.edgeId)}
          onReverse={() => onReverseRelationship?.(contextMenu.edgeId)}
          onDelete={() => onDeleteRelationship?.(contextMenu.edgeId)}
          onLineShapeChange={(shape) => onUpdateRelationship?.(contextMenu.edgeId, { lineShape: shape })}
          labelAction={isReviewMode ? null : labelAction(relationships.find((r) => r.id === contextMenu.edgeId)?.factStatus)}
          onLabelAction={onFactLabel && (() => {
            const action = labelAction(relationships.find((r) => r.id === contextMenu.edgeId)?.factStatus)
            if (action) onFactLabel('relationship', contextMenu.edgeId, action)
          })}
        />
      )}

      {contextMenu?.type === 'canvas' && (
        <CanvasContextMenu
          open
          position={contextMenu.position}
          parentKind={currentRootId ? elements.find((e) => e.id === currentRootId)?.kind ?? null : null}
          onClose={() => setContextMenu(null)}
          onAddElement={(kind) => onAddElementAtPosition?.(kind)}
          onTidy={() => onTidy?.()}
          onZoomToFit={() => fitView(fitAll())}
        />
      )}

      {createPopover && createPopover.rootId === currentRootId && (
        <CreatePopover
          open
          sourceKind={
            currentRootId
              ? elements.find((e) => e.id === currentRootId)?.kind ?? null
              : null
          }
          position={createPopover.screenPosition}
          onSelect={handlePopoverSelect}
          onClose={() => setCreatePopover(null)}
        />
      )}
    </div>
  )
}
