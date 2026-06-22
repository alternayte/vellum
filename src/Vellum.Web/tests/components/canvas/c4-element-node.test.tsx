import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ReactFlowProvider } from '@xyflow/react'
import { C4ElementNode } from '../../../src/components/canvas/nodes/c4-element-node'

import type { C4ElementData } from '../../../src/components/canvas/nodes/c4-element-node'

const mockData: C4ElementData = {
  id: '1',
  kind: 'system',
  name: 'Orders',
  description: 'Handles order lifecycle',
  technology: 'dotnet',
  status: 'current',
  tags: ['core'],
  ownerId: null,
}

function renderNode(data = mockData) {
  return render(
    <ReactFlowProvider>
      <C4ElementNode
        id="1"
        data={data}
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
}

describe('C4ElementNode', () => {
  it('renders element name and kind', () => {
    renderNode()
    expect(screen.getByText('Orders')).toBeTruthy()
    expect(screen.getByText('system')).toBeTruthy()
  })

  it('renders description', () => {
    renderNode()
    expect(screen.getByText('Handles order lifecycle')).toBeTruthy()
  })

  it('renders without description', () => {
    renderNode({ ...mockData, description: null })
    expect(screen.getByText('Orders')).toBeTruthy()
  })
})
