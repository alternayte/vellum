import { describe, it, expect, beforeEach, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { Navigator } from '../../../src/components/shell/navigator'
import { useShellStore } from '../../../src/stores/shell-store'
import { useCanvasStore } from '../../../src/stores/canvas-store'

const ELEMENTS = [
  { id: '1', kind: 'actor', name: 'Customer', status: 'current', parentId: null },
  { id: '2', kind: 'system', name: 'Orders', status: 'current', parentId: null },
  { id: '3', kind: 'app', name: 'Orders API', status: 'current', parentId: '2' },
]

const VIEWS = [
  { id: 'v1', name: 'Context View' },
  { id: 'v2', name: 'Orders System' },
]

beforeEach(() => {
  useCanvasStore.getState().reset()
  useShellStore.getState().selectElement(null)
  // Reset navigator open state
  const store = useShellStore.getState()
  if (!store.navigatorOpen) store.toggleNavigator()
})

describe('Navigator', () => {
  it('renders nothing when navigatorOpen is false', () => {
    useShellStore.getState().toggleNavigator() // close it
    const { container } = render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders top-level elements', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Customer')).toBeTruthy()
    expect(screen.getByText('Orders')).toBeTruthy()
  })

  it('renders child elements nested under parent', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Orders API')).toBeTruthy()
  })

  it('renders views section', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Context View')).toBeTruthy()
    expect(screen.getByText('Orders System')).toBeTruthy()
  })

  it('clicking an element calls selectElement with its id', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    fireEvent.click(screen.getByText('Customer'))
    expect(useShellStore.getState().selectedElementId).toBe('1')
  })

  it('double-clicking a parent element drills into it', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    fireEvent.dblClick(screen.getByText('Orders'))
    expect(useCanvasStore.getState().currentRootId).toBe('2')
    expect(useCanvasStore.getState().drillPath).toHaveLength(1)
  })

  it('double-clicking a leaf element does not drill (no children)', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    fireEvent.dblClick(screen.getByText('Customer'))
    expect(useCanvasStore.getState().drillPath).toHaveLength(0)
  })

  it('renders the + button for adding views', () => {
    render(<Navigator elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('+')).toBeTruthy()
  })
})
