import { describe, it, expect, beforeEach } from 'vitest'
import { render as renderInDom, screen, fireEvent } from '@testing-library/react'
import type { ReactElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { FilesFlyout } from '../../../src/components/shell/files-flyout'
import { useShellStore } from '../../../src/stores/shell-store'

const views = [
  { id: 'v1', name: 'Context Diagram' },
  { id: 'v2', name: 'Container Diagram' },
]

const docs = [
  { id: 'd1', title: 'SDD: API Server', type: 'sdd', spaceId: null, elementId: null },
  { id: 'd2', title: 'PRD: Checkout', type: 'prd', spaceId: null, elementId: null },
  { id: 'd3', title: 'Notes', type: null, spaceId: null, elementId: null },
]

const render = (ui: ReactElement) =>
  renderInDom(<QueryClientProvider client={new QueryClient()}>{ui}</QueryClientProvider>)

beforeEach(() => {
  useShellStore.getState().reset()
})

describe('FilesFlyout', () => {
  it('renders all views and docs', () => {
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    expect(screen.getByText('Context Diagram')).toBeDefined()
    expect(screen.getByText('Container Diagram')).toBeDefined()
    expect(screen.getByText('SDD: API Server')).toBeDefined()
    expect(screen.getByText('PRD: Checkout')).toBeDefined()
    expect(screen.getByText('Notes')).toBeDefined()
  })

  it('sets active view when a diagram is clicked', () => {
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    fireEvent.click(screen.getByText('Context Diagram'))
    expect(useShellStore.getState().activeViewId).toBe('v1')
  })

  it('opens doc when a doc is clicked', () => {
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    fireEvent.click(screen.getByText('SDD: API Server'))
    expect(useShellStore.getState().activeDocId).toBe('d1')
  })

  it('highlights the active view', () => {
    useShellStore.getState().setActiveView('v1')
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    const item = screen.getByText('Context Diagram').closest('button')
    expect(item?.className).toContain('bg-accent')
  })

  it('highlights the active doc', () => {
    useShellStore.getState().openDoc('d1')
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    const item = screen.getByText('SDD: API Server').closest('button')
    expect(item?.className).toContain('bg-accent')
  })

  it('shows doc type badges', () => {
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    expect(screen.getByText('sdd')).toBeDefined()
    expect(screen.getByText('prd')).toBeDefined()
  })

  it('filters items by search query', () => {
    render(<FilesFlyout projectId="p1" views={views} docs={docs} spaces={[]} elements={[]} canEdit />)
    const input = screen.getByPlaceholderText('Filter...')
    fireEvent.change(input, { target: { value: 'api' } })
    expect(screen.queryByText('Context Diagram')).toBeNull()
    expect(screen.getByText('SDD: API Server')).toBeDefined()
  })
})
