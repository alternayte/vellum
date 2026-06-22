import type { NodeTypes } from '@xyflow/react'
import { C4ElementNode } from './c4-element-node'
import { C4LabelChipNode } from './c4-label-chip-node'

export const nodeTypes: NodeTypes = {
  'c4-element': C4ElementNode,
  'c4-label-chip': C4LabelChipNode,
}
