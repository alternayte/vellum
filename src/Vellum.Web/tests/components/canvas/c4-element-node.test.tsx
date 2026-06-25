import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
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

function makeProps(dataOverrides: Partial<C4ElementData> = {}) {
  return {
    id: '1',
    data: { ...mockData, ...dataOverrides },
    type: 'c4-element' as const,
    isConnectable: true,
    positionAbsoluteX: 0,
    positionAbsoluteY: 0,
    zIndex: 0,
    draggable: true,
    dragging: false,
    selectable: true,
    deletable: true,
    selected: false,
  }
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

  it('renders four connection handles', () => {
    const { container } = renderNode()
    const handles = container.querySelectorAll('.react-flow__handle')
    expect(handles).toHaveLength(4)
  })

  it('renders system nodes at 320px width', () => {
    const { container } = renderNode({ ...mockData, kind: 'system' })
    const node = container.firstElementChild as HTMLElement
    expect(node.style.width).toBe('320px')
  })

  it('renders component nodes at 240px width', () => {
    const { container } = renderNode({ ...mockData, kind: 'component' })
    const node = container.firstElementChild as HTMLElement
    expect(node.style.width).toBe('240px')
  })

  it('renders actor nodes at 240px width', () => {
    const { container } = renderNode({ ...mockData, kind: 'actor' })
    const node = container.firstElementChild as HTMLElement
    expect(node.style.width).toBe('240px')
  })

  it('enters edit mode on name double-click', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    expect(getByDisplayValue('API Gateway')).toBeTruthy()
  })

  it('commits name on Enter', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    const input = getByDisplayValue('API Gateway')
    fireEvent.change(input, { target: { value: 'New Name' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onRename).toHaveBeenCalledWith('New Name')
  })

  it('cancels edit on Escape', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    const input = getByDisplayValue('API Gateway')
    fireEvent.change(input, { target: { value: 'Changed' } })
    fireEvent.keyDown(input, { key: 'Escape' })
    expect(onRename).not.toHaveBeenCalled()
    expect(getByText('API Gateway')).toBeTruthy()
  })

  it('commits name on blur', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    const input = getByDisplayValue('API Gateway')
    fireEvent.change(input, { target: { value: 'Blurred Name' } })
    fireEvent.blur(input)
    expect(onRename).toHaveBeenCalledWith('Blurred Name')
  })

  it('does not commit if name unchanged', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    const input = getByDisplayValue('API Gateway')
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onRename).not.toHaveBeenCalled()
  })

  it('does not commit empty name', () => {
    const onRename = vi.fn()
    const { getByText, getByDisplayValue } = render(
      <ReactFlowProvider>
        <C4ElementNode {...makeProps({ name: 'API Gateway', onRename })} />
      </ReactFlowProvider>,
    )
    fireEvent.doubleClick(getByText('API Gateway'))
    const input = getByDisplayValue('API Gateway')
    fireEvent.change(input, { target: { value: '  ' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onRename).not.toHaveBeenCalled()
  })
})
