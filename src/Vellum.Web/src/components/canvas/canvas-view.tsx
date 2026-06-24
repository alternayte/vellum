import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  ReactFlow,
  ReactFlowProvider,
  MiniMap,
  Background,
  BackgroundVariant,
  type Node,
  type Edge,
  type NodeMouseHandler,
  type OnConnect,
  MarkerType,
  useReactFlow,
  SelectionMode,
} from '@xyflow/react'
import { NodeContextMenu } from './context-menus/node-context-menu'
import { EdgeContextMenu } from './context-menus/edge-context-menu'
import { CanvasContextMenu } from './context-menus/canvas-context-menu'
import '@xyflow/react/dist/style.css'
import { nodeTypes } from './nodes/node-types'
import { edgeTypes } from './edges/edge-types'
import { BulkActionsToolbar } from './bulk-actions-toolbar'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { useLod } from '@/hooks/use-lod'
import { Button } from '@/components/ui/button'
import type { C4ElementData } from './nodes/c4-element-node'
import type { C4ContainerData } from './nodes/c4-container-node'
import type { DiffState } from '@/stores/draft-store'

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
}

interface RelationshipModel {
  id: string
  fromId: string
  toId: string
  label: string | null
  technology: string | null
}

interface LayoutPosition {
  elementId: string
  x: number
  y: number
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
  onNodeDoubleClick?: (elementId: string) => void
  onFitViewReady?: (fitView: () => void) => void
  onAddElement?: () => void
  onConnect?: (source: string, target: string) => void
  onBulkDelete?: (ids: string[]) => void
  onDuplicateElement?: (id: string) => void
  onDeleteElement?: (id: string) => void
  onReverseRelationship?: (id: string) => void
  onDeleteRelationship?: (id: string) => void
  onAddElementAtPosition?: (kind: string) => void
  onTidy?: () => void
}

export function CanvasView(props: CanvasViewProps) {
  return (
    <ReactFlowProvider>
      <CanvasViewInner {...props} />
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
  onNodeDoubleClick,
  onFitViewReady,
  onAddElement,
  onConnect: onConnectProp,
  onBulkDelete,
  onDuplicateElement,
  onDeleteElement,
  onReverseRelationship,
  onDeleteRelationship,
  onAddElementAtPosition,
  onTidy,
}: CanvasViewProps) {
  const { currentRootId, zoomLevel, setZoom, activeLens, expandedNodeIds, toggleExpand } = useCanvasStore()
  const { fitView } = useReactFlow()
  const [selectedNodeIds, setSelectedNodeIds] = useState<string[]>([])
  const [contextMenu, setContextMenu] = useState<ContextMenuState>(null)

  useEffect(() => {
    onFitViewReady?.(() => fitView())
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fitView])
  const { selectElement, selectRelationship, selectMessage } = useShellStore()
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

  const positionMap = useMemo(() => {
    const map = new Map<string, { x: number; y: number }>()
    for (const p of positions) map.set(p.elementId, { x: p.x, y: p.y })
    return map
  }, [positions])

  const nodes: Node[] = useMemo(() => {
    const result: Node[] = []

    for (const [index, el] of visibleElements.entries()) {
      const isExpanded = expandedNodeIds.has(el.id)
      const col = index % 3
      const row = Math.floor(index / 3)
      const pos = positionMap.get(el.id) ?? { x: col * 340, y: row * 200 }
      const entityDiffState = diffStateMap?.get(el.id) ?? (isReviewMode ? 'unchanged' : undefined)

      if (isExpanded) {
        // Render as container boundary
        result.push({
          id: el.id,
          type: 'c4-container',
          position: pos,
          data: {
            id: el.id,
            kind: el.kind,
            name: el.name,
          } satisfies C4ContainerData,
          style: { width: 'auto', height: 'auto' },
        })

        // Add children inside the container
        const children = elements.filter((child) => child.parentId === el.id)
        children.forEach((child, ci) => {
          const childPos = positionMap.get(child.id) ?? { x: 40 + (ci % 3) * 300, y: 60 + Math.floor(ci / 3) * 180 }
          const childDiffState = diffStateMap?.get(child.id) ?? (isReviewMode ? 'unchanged' : undefined)
          result.push({
            id: child.id,
            type: tier === 'full' ? 'c4-element' : 'c4-label-chip',
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
              diffState: childDiffState,
            } satisfies C4ElementData,
          })
        })
      } else {
        // Render as normal element
        result.push({
          id: el.id,
          type: tier === 'full' ? 'c4-element' : 'c4-label-chip',
          position: pos,
          data: {
            id: el.id,
            kind: el.kind,
            name: el.name,
            description: el.description,
            technology: el.technology,
            status: el.status,
            tags: el.tags,
            ownerId: el.ownerId,
            diffState: entityDiffState,
          } satisfies C4ElementData,
        })
      }
    }

    return result
  }, [visibleElements, elements, positionMap, tier, diffStateMap, isReviewMode, expandedNodeIds])

  const edges: Edge[] = useMemo(() => {
    return visibleRelationships.map((rel) => ({
      id: rel.id,
      source: rel.fromId,
      target: rel.toId,
      type: 'c4-edge',
      markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--border)' },
      data: {
        label: rel.label,
        technology: rel.technology,
        diffState: diffStateMap?.get(rel.id) ?? (isReviewMode ? 'unchanged' : undefined),
      },
    }))
  }, [visibleRelationships, diffStateMap, isReviewMode])

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
          markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--border)' },
          data: { label: null, technology: null },
        }
        const consumerEdges: Edge[] = msg.consumerIds
          .filter((cid) => visibleIds.has(cid))
          .map((cid) => ({
            id: `msg-edge-c-${msg.id}-${cid}`,
            source: `msg-${msg.id}`,
            target: cid,
            type: 'c4-edge',
            markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--border)' },
            data: { label: null, technology: null },
          }))
        return [producerEdge, ...consumerEdges]
      })
  }, [activeLens, messages, visibleIds])

  const allNodes = useMemo(() => [...nodes, ...messageNodes], [nodes, messageNodes])
  const allEdges = useMemo(() => [...edges, ...messageEdges], [edges, messageEdges])

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

  const handlePaneClick = useCallback(() => {
    selectElement(null)
    selectRelationship(null)
    selectMessage(null)
  }, [selectElement, selectRelationship, selectMessage])

  const handleSelectionChange = useCallback(({ nodes }: { nodes: Node[] }) => {
    setSelectedNodeIds(nodes.map((n) => n.id).filter((id) => !id.startsWith('msg-')))
  }, [])

  const handleNodeContextMenu = useCallback(
    (event: React.MouseEvent, node: Node) => {
      event.preventDefault()
      setContextMenu({ type: 'node', nodeId: node.id, position: { x: event.clientX, y: event.clientY } })
    },
    [],
  )

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
            {isTopLevel
              ? 'Start with a system. Draw a connection to add the next one.'
              : 'Nothing here yet. Add an element to get started.'}
          </p>
          <Button
            variant="outline"
            size="sm"
            className="mt-3"
            onClick={() => onAddElement?.()}
          >
            {isTopLevel ? 'Add System' : 'Add Element'}
          </Button>
        </div>
      </div>
    )
  }

  return (
    <>
      <ReactFlow
        nodes={allNodes}
        edges={allEdges}
        nodeTypes={nodeTypes}
        edgeTypes={edgeTypes}
        onNodeClick={handleNodeClick}
        onNodeDoubleClick={handleNodeDoubleClick}
        onNodeDragStop={onNodeDragStop ? (_event, node) => onNodeDragStop(node.id, node.position.x, node.position.y) : undefined}
        onConnect={handleConnect}
        onEdgeClick={handleEdgeClick}
        onPaneClick={handlePaneClick}
        onMoveEnd={(_event, viewport) => setZoom(viewport.zoom)}
        onSelectionChange={handleSelectionChange}
        onNodeContextMenu={handleNodeContextMenu}
        onEdgeContextMenu={handleEdgeContextMenu}
        onPaneContextMenu={handlePaneContextMenu}
        selectionOnDrag
        selectionMode={SelectionMode.Partial}
        multiSelectionKeyCode="Shift"
        panOnDrag={[1, 2]}
        fitView
        snapToGrid
        snapGrid={[16, 16]}
        minZoom={0.1}
        maxZoom={3}
      >
        <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="var(--border)" />
        <MiniMap
          nodeColor="var(--card)"
          maskColor="color-mix(in srgb, var(--background) 80%, transparent)"
          className="!bg-card !border-border"
        />
        <BulkActionsToolbar
          selectedCount={selectedNodeIds.length}
          onDelete={() => onBulkDelete?.(selectedNodeIds)}
        />
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
            onClose={() => setContextMenu(null)}
            onEditDetails={() => selectElement(nodeId)}
            onExpand={() => toggleExpand(nodeId)}
            onDrillInto={() => {
              const { drillInto } = useCanvasStore.getState()
              drillInto({ elementId: el.id, name: el.name, kind: el.kind })
            }}
            onDuplicate={() => onDuplicateElement?.(nodeId)}
            onDelete={() => onDeleteElement?.(nodeId)}
          />
        )
      })()}

      {contextMenu?.type === 'edge' && (
        <EdgeContextMenu
          open
          position={contextMenu.position}
          onClose={() => setContextMenu(null)}
          onEditDetails={() => selectRelationship(contextMenu.edgeId)}
          onReverse={() => onReverseRelationship?.(contextMenu.edgeId)}
          onDelete={() => onDeleteRelationship?.(contextMenu.edgeId)}
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
          onZoomToFit={() => fitView()}
        />
      )}
    </>
  )
}
