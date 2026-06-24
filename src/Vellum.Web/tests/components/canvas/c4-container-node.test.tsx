import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ReactFlowProvider } from '@xyflow/react'
import { C4ContainerNode } from '../../../src/components/canvas/nodes/c4-container-node'

function renderContainer(data = { id: 's1', kind: 'system', name: 'Orders' }) {
  return render(
    <ReactFlowProvider>
      <C4ContainerNode
        id="s1"
        data={data}
        type="c4-container"
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

describe('C4ContainerNode', () => {
  it('renders kind and name as label', () => {
    renderContainer()
    expect(screen.getByText('system')).toBeTruthy()
    expect(screen.getByText('Orders')).toBeTruthy()
  })

  it('renders with dashed border', () => {
    const { container } = renderContainer()
    const node = container.firstElementChild as HTMLElement
    expect(node.style.borderStyle).toBe('dashed')
  })
})
