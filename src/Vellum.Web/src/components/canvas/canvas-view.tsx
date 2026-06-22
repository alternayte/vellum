import { useCallback, useMemo } from 'react'
import {
  ReactFlow,
  MiniMap,
  Background,
  BackgroundVariant,
  type Node,
  type Edge,
  type NodeMouseHandler,
  MarkerType,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { nodeTypes } from './nodes/node-types'
import { edgeTypes } from './edges/edge-types'
import { useCanvasStore } from '@/stores/canvas-store'
import { useShellStore } from '@/stores/shell-store'
import { useLod } from '@/hooks/use-lod'
import { Button } from '@/components/ui/button'
import type { C4ElementData } from './nodes/c4-element-node'

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

interface CanvasViewProps {
  elements: ElementModel[]
  relationships: RelationshipModel[]
  positions: LayoutPosition[]
  onNodeDragStop?: (id: string, x: number, y: number) => void
  onNodeDoubleClick?: (elementId: string) => void
}

export function CanvasView({
  elements,
  relationships,
  positions,
  onNodeDragStop,
  onNodeDoubleClick,
}: CanvasViewProps) {
  const { currentRootId, zoomLevel, setZoom } = useCanvasStore()
  const { selectElement, selectRelationship } = useShellStore()
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
        } satisfies C4ElementData,
      }
    })
  }, [visibleElements, positionMap, tier])

  const edges: Edge[] = useMemo(() => {
    return visibleRelationships.map((rel) => ({
      id: rel.id,
      source: rel.fromId,
      target: rel.toId,
      type: 'c4-edge',
      markerEnd: { type: MarkerType.ArrowClosed, color: 'hsl(var(--border))' },
      data: { label: rel.label, technology: rel.technology },
    }))
  }, [visibleRelationships])

  const handleNodeClick: NodeMouseHandler = useCallback(
    (_event, node) => selectElement(node.id),
    [selectElement],
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
  }, [selectElement, selectRelationship])

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
      nodes={nodes}
      edges={edges}
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
