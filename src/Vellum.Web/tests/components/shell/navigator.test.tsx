import { describe, it, expect, beforeEach, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { Navigator } from '../../../src/components/shell/navigator'
import { useShellStore } from '../../../src/stores/shell-store'
import { useCanvasStore } from '../../../src/stores/canvas-store'

vi.mock('../../../src/hooks/use-drafts', () => ({
  useDrafts: () => ({ data: { items: [] } }),
  useDraft: () => ({ data: undefined }),
  useCreateDraft: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useAbandonDraft: () => ({ mutateAsync: vi.fn(), isPending: false }),
}))
vi.mock('../../../src/api/client', () => ({}))

const ELEMENTS = [
  { id: '1', kind: 'actor', name: 'Customer', status: 'current', parentId: null },
  { id: '2', kind: 'system', name: 'Orders', status: 'current', parentId: null },
  { id: '3', kind: 'app', name: 'Orders API', status: 'current', parentId: '2' },
]

const VIEWS = [
  { id: 'v1', name: 'Context View' },
  { id: 'v2', name: 'Orders System' },
]

const SPACES = [
  { id: 's1', name: 'Architecture' },
]

const DOCS = [
  { id: 'd1', title: 'System Overview', spaceId: 's1' },
  { id: 'd2', title: 'ADR-001', spaceId: null },
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
    const { container } = render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders top-level elements', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Customer')).toBeTruthy()
    expect(screen.getByText('Orders')).toBeTruthy()
  })

  it('renders child elements nested under parent', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Orders API')).toBeTruthy()
  })

  it('renders views section', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getByText('Context View')).toBeTruthy()
    expect(screen.getByText('Orders System')).toBeTruthy()
  })

  it('clicking an element calls selectElement with its id', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    fireEvent.click(screen.getByText('Customer'))
    expect(useShellStore.getState().selectedElementId).toBe('1')
  })

  it('double-clicking a parent element drills into it', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    fireEvent.dblClick(screen.getByText('Orders'))
    expect(useCanvasStore.getState().currentRootId).toBe('2')
    expect(useCanvasStore.getState().drillPath).toHaveLength(1)
  })

  it('double-clicking a leaf element does not drill (no children)', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    fireEvent.dblClick(screen.getByText('Customer'))
    expect(useCanvasStore.getState().drillPath).toHaveLength(0)
  })

  it('renders the + button for adding views', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} />)
    expect(screen.getAllByText('+').length).toBeGreaterThan(0)
  })

  it('renders the Docs section header', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} spaces={SPACES} docs={DOCS} />)
    expect(screen.getByText('Docs')).toBeTruthy()
  })

  it('renders space names in docs section', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} spaces={SPACES} docs={DOCS} />)
    expect(screen.getByText('Architecture')).toBeTruthy()
  })

  it('shows loose docs (spaceId null) directly in docs section', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} spaces={SPACES} docs={DOCS} />)
    expect(screen.getByText('ADR-001')).toBeTruthy()
  })

  it('clicking a doc calls openDoc on shell store', () => {
    render(<Navigator projectId="p1" elements={ELEMENTS} views={VIEWS} spaces={[]} docs={[{ id: 'd2', title: 'ADR-001', spaceId: null }]} />)
    fireEvent.click(screen.getByText('ADR-001'))
    expect(useShellStore.getState().activeDocId).toBe('d2')
  })
})
