import ELK from 'elkjs/lib/elk.bundled.js'

const elk = new ELK()

interface LayoutElement {
  id: string
  width?: number
  height?: number
}

interface LayoutRelationship {
  id: string
  fromId: string
  toId: string
}

interface LayoutPosition {
  elementId: string
  x: number
  y: number
}

export async function computeLayout(
  elements: LayoutElement[],
  relationships: LayoutRelationship[],
): Promise<LayoutPosition[]> {
  const graph = {
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'RIGHT',
      'elk.spacing.nodeNode': '60',
      'elk.layered.spacing.nodeNodeBetweenLayers': '100',
    },
    children: elements.map((el) => ({
      id: el.id,
      width: el.width ?? 280,
      height: el.height ?? 120,
    })),
    edges: relationships.map((rel) => ({
      id: rel.id,
      sources: [rel.fromId],
      targets: [rel.toId],
    })),
  }

  const result = await elk.layout(graph)

  return (result.children ?? []).map((node) => ({
    elementId: node.id,
    x: node.x ?? 0,
    y: node.y ?? 0,
  }))
}
