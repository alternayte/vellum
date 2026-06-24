import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { BulkActionsToolbar } from '../../../src/components/canvas/bulk-actions-toolbar'

describe('BulkActionsToolbar', () => {
  it('renders nothing when fewer than 2 nodes selected', () => {
    const { container } = render(
      <BulkActionsToolbar selectedCount={1} onDelete={vi.fn()} />
    )
    expect(container.firstChild).toBeNull()
  })

  it('renders selected count when 2+ nodes selected', () => {
    render(<BulkActionsToolbar selectedCount={3} onDelete={vi.fn()} />)
    expect(screen.getByText('3 selected')).toBeTruthy()
  })

  it('calls onDelete when delete button clicked', () => {
    const onDelete = vi.fn()
    render(<BulkActionsToolbar selectedCount={2} onDelete={onDelete} />)
    fireEvent.click(screen.getByRole('button', { name: /delete/i }))
    expect(onDelete).toHaveBeenCalledOnce()
  })
})
