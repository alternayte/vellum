import { describe, it, expect, beforeEach, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { DetailPanel } from '../../../src/components/shell/detail-panel'
import { useShellStore } from '../../../src/stores/shell-store'

vi.mock('../../../src/hooks/use-draft-comments', () => ({
  useComments: () => ({ data: { items: [] } }),
  useCreateComment: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateComment: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteComment: () => ({ mutate: vi.fn(), isPending: false }),
}))
vi.mock('../../../src/hooks/use-auth', () => ({
  useAuth: () => ({ user: null }),
}))
vi.mock('../../../src/api/client', () => ({}))

const ELEMENTS = [
  {
    id: 'e1',
    kind: 'system',
    name: 'Orders',
    description: 'Order lifecycle',
    technology: 'dotnet',
    status: 'current',
    tags: ['core', 'backend'],
    parentId: null,
  },
]

const RELATIONSHIPS = [
  {
    id: 'r1',
    fromId: 'e1',
    toId: 'e2',
    label: 'Places orders',
    technology: 'HTTPS',
  },
]

beforeEach(() => {
  useShellStore.getState().selectElement(null)
  useShellStore.getState().selectRelationship(null)
})

describe('DetailPanel', () => {
  it('renders nothing visible when no selection', () => {
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} />)
    // Sheet is closed — element name should not be visible
    expect(screen.queryByText('Orders')).toBeNull()
  })

  it('shows element name when an element is selected', () => {
    useShellStore.getState().selectElement('e1')
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} />)
    expect(screen.getByText('Orders')).toBeTruthy()
  })

  it('shows element kind badge when element is selected', () => {
    useShellStore.getState().selectElement('e1')
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} />)
    expect(screen.getByText('system')).toBeTruthy()
  })

  it('shows element tags when element is selected', () => {
    useShellStore.getState().selectElement('e1')
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} />)
    expect(screen.getByText('core')).toBeTruthy()
    expect(screen.getByText('backend')).toBeTruthy()
  })

  it('shows relationship label when a relationship is selected', () => {
    useShellStore.getState().selectRelationship('r1')
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} />)
    expect(screen.getByText('Relationship')).toBeTruthy()
  })

  it('calls onUpdateElement on name input blur', () => {
    useShellStore.getState().selectElement('e1')
    const onUpdate = vi.fn()
    render(<DetailPanel projectId="p1" elements={ELEMENTS} relationships={RELATIONSHIPS} onUpdateElement={onUpdate} />)
    const nameInput = screen.getAllByRole('textbox').find(
      (el) => (el as HTMLInputElement).value === 'Orders',
    ) as HTMLInputElement
    fireEvent.blur(nameInput, { target: { value: 'New Orders' } })
    expect(onUpdate).toHaveBeenCalledWith('e1', { name: 'New Orders' })
  })
})
