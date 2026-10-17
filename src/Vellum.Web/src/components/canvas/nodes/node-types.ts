import type { NodeTypes } from '@xyflow/react'
import { C4ElementNode } from './c4-element-node'
import { C4LabelChipNode } from './c4-label-chip-node'
import { C4ContainerNode } from './c4-container-node'
import { MessagePillNode } from './message-pill-node'
import { C4BoundaryNode } from './c4-boundary-node'
import { GhostContainerNode, GhostElementNode } from './ghost-node'

export const nodeTypes: NodeTypes = {
  'c4-element': C4ElementNode,
  'c4-label-chip': C4LabelChipNode,
  'c4-container': C4ContainerNode,
  'message-pill': MessagePillNode,
  'c4-boundary': C4BoundaryNode,
  'ghost-element': GhostElementNode,
  'ghost-container': GhostContainerNode,
}
