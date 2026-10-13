import { describe, it, expect, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { NodeContextMenu } from '../../../src/components/canvas/context-menus/node-context-menu'

describe('NodeContextMenu', () => {
  it('renders menu items when open', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={true}
        isExpanded={false}
        childKinds={['app', 'store']}
        canExpand
        onAddChild={vi.fn()}
        onClose={vi.fn()}
        onEditDetails={vi.fn()}
        onExpand={vi.fn()}
        onDrillInto={vi.fn()}
        onDuplicate={vi.fn()}
        onDelete={vi.fn()}
      />
    )
    expect(screen.getByText('Edit details')).toBeTruthy()
    expect(screen.getByText('Expand')).toBeTruthy()
    expect(screen.getByText('Drill into')).toBeTruthy()
    expect(screen.getByText('Duplicate')).toBeTruthy()
    expect(screen.getByText('Delete')).toBeTruthy()
  })

  it('hides expand/drill for a node that can hold no element', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={false}
        isExpanded={false}
        childKinds={[]}
        canExpand
        onAddChild={vi.fn()}
        onClose={vi.fn()}
        onEditDetails={vi.fn()}
        onExpand={vi.fn()}
        onDrillInto={vi.fn()}
        onDuplicate={vi.fn()}
        onDelete={vi.fn()}
      />
    )
    expect(screen.getByText('Edit details')).toBeTruthy()
    expect(screen.queryByText('Expand')).toBeNull()
    expect(screen.queryByText('Drill into')).toBeNull()
  })

  it('offers a first child and the level of an empty system', () => {
    const onAddChild = vi.fn()
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={false}
        isExpanded={false}
        childKinds={['app', 'store']}
        canExpand
        onAddChild={onAddChild}
        onClose={vi.fn()}
        onEditDetails={vi.fn()}
        onExpand={vi.fn()}
        onDrillInto={vi.fn()}
        onDuplicate={vi.fn()}
        onDelete={vi.fn()}
      />
    )
    fireEvent.click(screen.getByText('Add store inside'))
    expect(onAddChild).toHaveBeenCalledWith('store')
    expect(screen.getByText('Add app inside')).toBeTruthy()
    expect(screen.getByText('Drill into')).toBeTruthy()
    expect(screen.queryByText('Expand')).toBeNull()
  })

  it('shows Collapse when expanded', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={true}
        isExpanded={true}
        childKinds={['app', 'store']}
        canExpand
        onAddChild={vi.fn()}
        onClose={vi.fn()}
        onEditDetails={vi.fn()}
        onExpand={vi.fn()}
        onDrillInto={vi.fn()}
        onDuplicate={vi.fn()}
        onDelete={vi.fn()}
      />
    )
    expect(screen.getByText('Collapse')).toBeTruthy()
  })
})
