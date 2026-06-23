import { useCallback, useEffect, useMemo } from 'react'
import {
  ReactFlow,
  MiniMap,
  Background,
  BackgroundVariant,
  type Node,
  type Edge,
  type NodeMouseHandler,
  MarkerType,
  useReactFlow,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { nodeTypes } from './nodes/node-types'
import { edgeTypes } from './edges/edge-types'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { useLod } from '@/hooks/use-lod'
import { Button } from '@/components/ui/button'
import type { C4ElementData } from './nodes/c4-element-node'
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
}

export function CanvasView({
  elements,
  relationships,
  positions,
  diffStateMap,
  isReviewMode,
  messages,
  onNodeDragStop,
  onNodeDoubleClick,
  onFitViewReady,
}: CanvasViewProps) {
  const { currentRootId, zoomLevel, setZoom, activeLens } = useCanvasStore()
  const { fitView } = useReactFlow()

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

  const visibleIds = useMemo(() => new Set(visibleElements.map((e) => e.id)), [visibleElements])

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
    return visibleElements.map((el, index) => {
      const pos = positionMap.get(el.id) ?? { x: index * 320, y: index * 100 }
      const entityDiffState = diffStateMap?.get(el.id) ?? (isReviewMode ? 'unchanged' : undefined)
      return {
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
      }
    })
  }, [visibleElements, positionMap, tier, diffStateMap, isReviewMode])

  const edges: Edge[] = useMemo(() => {
    return visibleRelationships.map((rel) => ({
      id: rel.id,
      source: rel.fromId,
      target: rel.toId,
      type: 'c4-edge',
      markerEnd: { type: MarkerType.ArrowClosed, color: 'hsl(var(--border))' },
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
          markerEnd: { type: MarkerType.ArrowClosed, color: 'hsl(var(--border))' },
          data: { label: null, technology: null },
        }
        const consumerEdges: Edge[] = msg.consumerIds
          .filter((cid) => visibleIds.has(cid))
          .map((cid) => ({
            id: `msg-edge-c-${msg.id}-${cid}`,
            source: `msg-${msg.id}`,
            target: cid,
            type: 'c4-edge',
            markerEnd: { type: MarkerType.ArrowClosed, color: 'hsl(var(--border))' },
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
    (_event, node) => onNodeDoubleClick?.(node.id),
    [onNodeDoubleClick],
  )

  const handleEdgeClick = useCallback(
    (_event: React.MouseEvent, edge: Edge) => selectRelationship(edge.id),
    [selectRelationship],
  )

  const handlePaneClick = useCallback(() => {
    selectElement(null)
    selectRelationship(null)
    selectMessage(null)
  }, [selectElement, selectRelationship, selectMessage])

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
            onClick={() => {/* open create dialog */}}
          >
            {isTopLevel ? 'Add System' : 'Add Element'}
          </Button>
        </div>
      </div>
    )
  }

  return (
    <ReactFlow
      nodes={allNodes}
      edges={allEdges}
      nodeTypes={nodeTypes}
      edgeTypes={edgeTypes}
      onNodeClick={handleNodeClick}
      onNodeDoubleClick={handleNodeDoubleClick}
      onNodeDragStop={onNodeDragStop ? (_event, node) => onNodeDragStop(node.id, node.position.x, node.position.y) : undefined}
      onEdgeClick={handleEdgeClick}
      onPaneClick={handlePaneClick}
      onMoveEnd={(_event, viewport) => setZoom(viewport.zoom)}
      fitView
      snapToGrid
      snapGrid={[16, 16]}
      minZoom={0.1}
      maxZoom={3}
    >
      <Background variant={BackgroundVariant.Dots} gap={16} size={1} color="hsl(var(--border))" />
      <MiniMap
        nodeColor="hsl(var(--card))"
        maskColor="hsl(var(--background) / 0.8)"
        className="!bg-card !border-border"
      />
    </ReactFlow>
  )
}
