import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ReactFlowProvider } from '@xyflow/react'
import { C4LabelChipNode } from '../../../src/components/canvas/nodes/c4-label-chip-node'

describe('C4LabelChipNode', () => {
  it('renders element name', () => {
    render(
      <ReactFlowProvider>
        <C4LabelChipNode
          id="1"
          data={{ id: '1', kind: 'system', name: 'Orders', description: null, technology: null, status: 'current', tags: [], ownerId: null }}
          type="c4-element"
          isConnectable={true}
          positionAbsoluteX={0}
          positionAbsoluteY={0}
          zIndex={0}
          draggable={true}
          dragging={false}
          selectable={true}
          deletable={true}
          selected={false}
        />
      </ReactFlowProvider>,
    )
    expect(screen.getByText('Orders')).toBeTruthy()
  })
})
