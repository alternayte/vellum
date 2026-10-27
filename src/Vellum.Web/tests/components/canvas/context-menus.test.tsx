import { describe, it, expect, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import type { ComponentProps } from 'react'
import { NodeContextMenu } from '../../../src/components/canvas/context-menus/node-context-menu'

type Props = ComponentProps<typeof NodeContextMenu>

function menu(overrides: Partial<Props>) {
  const props: Props = {
    position: { x: 100, y: 100 },
    hasChildren: false,
    isExpanded: false,
    childKinds: [],
    canExpand: true,
    canOpenInside: false,
    onAddChild: vi.fn(),
    onClose: vi.fn(),
    onEditDetails: vi.fn(),
    onExpand: vi.fn(),
    onOpenInside: vi.fn(),
    onSelectConnected: vi.fn(),
    onCopyId: vi.fn(),
    onDuplicate: vi.fn(),
    onDelete: vi.fn(),
    ...overrides,
  }
  return render(<NodeContextMenu {...props} />)
}

describe('NodeContextMenu', () => {
  it('renders menu items', () => {
    menu({ hasChildren: true, childKinds: ['app', 'store'], canOpenInside: true })
    expect(screen.getByText('Edit details')).toBeTruthy()
    expect(screen.getByText('Expand')).toBeTruthy()
    expect(screen.getByText('Open inside')).toBeTruthy()
    expect(screen.getByText('Duplicate')).toBeTruthy()
    expect(screen.getByText('Delete')).toBeTruthy()
  })

  it('hides expand and open inside for a node that can hold no element', () => {
    menu({})
    expect(screen.getByText('Edit details')).toBeTruthy()
    expect(screen.queryByText('Expand')).toBeNull()
    expect(screen.queryByText('Open inside')).toBeNull()
  })

  it('offers a first child and the level of an empty system', () => {
    const onAddChild = vi.fn()
    menu({ childKinds: ['app', 'store'], canOpenInside: true, onAddChild })
    fireEvent.click(screen.getByText('Add store inside'))
    expect(onAddChild).toHaveBeenCalledWith('store')
    expect(screen.getByText('Add app inside')).toBeTruthy()
    expect(screen.getByText('Open inside')).toBeTruthy()
    expect(screen.queryByText('Expand')).toBeNull()
  })

  it('shows Collapse when expanded', () => {
    menu({ hasChildren: true, isExpanded: true, childKinds: ['app', 'store'], canOpenInside: true })
    expect(screen.getByText('Collapse')).toBeTruthy()
  })

  it('keeps open inside, select connected and copy ID on a read-only model', () => {
    menu({ readOnly: true, canOpenInside: true, onHide: undefined })
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Show details', 'Open inside', 'Select connected', 'Copy ID'])
  })

  it('is a menu of menu items that the arrow keys move through and Escape closes', () => {
    const onClose = vi.fn()
    menu({ onClose })
    const element = screen.getByRole('menu', { name: 'Element' })
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Edit details', 'Select connected', 'Copy ID', 'Duplicate', 'Delete'])
    expect(document.activeElement?.textContent).toBe('Edit details')
    fireEvent.keyDown(element, { key: 'ArrowDown' })
    expect(document.activeElement?.textContent).toBe('Select connected')
    fireEvent.keyDown(element, { key: 'ArrowUp' })
    fireEvent.keyDown(element, { key: 'ArrowUp' })
    expect(document.activeElement?.textContent).toBe('Delete')
    fireEvent.keyDown(element, { key: 'Home' })
    fireEvent.keyDown(element, { key: 'd' })
    expect(document.activeElement?.textContent).toBe('Duplicate')
    fireEvent.keyDown(element, { key: 'Escape' })
    expect(onClose).toHaveBeenCalled()
  })
})
