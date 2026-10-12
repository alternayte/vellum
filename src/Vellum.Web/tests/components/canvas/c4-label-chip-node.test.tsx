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

  it('keeps the truth marker at chip detail', () => {
    render(
      <ReactFlowProvider>
        <C4LabelChipNode
          id="1"
          data={{
            id: '1',
            kind: 'system',
            name: 'Orders',
            description: null,
            technology: null,
            status: 'current',
            tags: [],
            ownerId: null,
            factStatus: 'inferred',
            provenance: {
              source: 'github.com/acme/orders@abc123:src/Orders.cs:1-10',
              revision: 'abc123',
              confidence: 0.6,
              acceptedBy: null,
              acceptedAt: null,
            },
          }}
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
    expect(screen.getByRole('img', { name: 'Inferred' })).toBeTruthy()
  })
})
