import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { NodeContextMenu } from '../../../src/components/canvas/context-menus/node-context-menu'

describe('NodeContextMenu', () => {
  it('renders menu items when open', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={true}
        isExpanded={false}
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

  it('hides expand/drill when node has no children', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={false}
        isExpanded={false}
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

  it('shows Collapse when expanded', () => {
    render(
      <NodeContextMenu
        open={true}
        position={{ x: 100, y: 100 }}
        hasChildren={true}
        isExpanded={true}
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
