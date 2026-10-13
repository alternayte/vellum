import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { CatalogFlyout } from '../../../src/components/shell/catalog-flyout'
import { useCanvasStore } from '../../../src/stores/canvas-store'
import { useShellStore } from '../../../src/stores/shell-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
  useShellStore.getState().reset()
})

describe('CatalogFlyout', () => {
  it('shows top-level kinds when at context root', () => {
    const onAddElement = vi.fn()
    render(<CatalogFlyout elements={[]} onAddElement={onAddElement} />)
    expect(screen.getByText('Actor')).toBeDefined()
    expect(screen.getByText('System')).toBeDefined()
    expect(screen.queryByText('Component')).toBeNull()
  })

  it('shows container-level kinds when drilled into a system', () => {
    useCanvasStore.getState().drillInto({ elementId: 's1', name: 'API', kind: 'system' })
    const onAddElement = vi.fn()
    render(
      <CatalogFlyout
        elements={[{ id: 's1', kind: 'system', name: 'API' }]}
        onAddElement={onAddElement}
      />
    )
    expect(screen.getByText('App')).toBeDefined()
    expect(screen.getByText('Store')).toBeDefined()
    expect(screen.queryByText('Actor')).toBeNull()
  })

  it('calls onAddElement with the kind when clicked', () => {
    const onAddElement = vi.fn()
    render(<CatalogFlyout elements={[]} onAddElement={onAddElement} />)
    fireEvent.click(screen.getByText('System'))
    expect(onAddElement).toHaveBeenCalledWith('system', null)
  })

  it('shows keyboard shortcut hints', () => {
    render(<CatalogFlyout elements={[]} onAddElement={vi.fn()} />)
    expect(screen.getByText('⇧S')).toBeDefined()
  })

  it('adds inside the selected system from the top level', () => {
    useShellStore.getState().selectElement('s1')
    const onAddElement = vi.fn()
    render(<CatalogFlyout elements={[{ id: 's1', kind: 'system', name: 'Orders' }]} onAddElement={onAddElement} />)
    expect(screen.queryByText('Actor')).toBeNull()
    fireEvent.click(screen.getByText('Store'))
    expect(onAddElement).toHaveBeenCalledWith('store', 's1')
  })

  it('offers the kinds of the place that is chosen', () => {
    const onAddElement = vi.fn()
    render(<CatalogFlyout elements={[{ id: 's1', kind: 'system', name: 'Orders' }]} onAddElement={onAddElement} />)
    fireEvent.change(screen.getByLabelText('Add inside'), { target: { value: 's1' } })
    fireEvent.click(screen.getByText('App'))
    expect(onAddElement).toHaveBeenCalledWith('app', 's1')
  })
})
