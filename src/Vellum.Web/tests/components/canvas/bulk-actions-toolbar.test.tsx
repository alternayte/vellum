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

  it('renders status dropdown when 2+ nodes selected', () => {
    render(
      <BulkActionsToolbar
        selectedCount={3}
        onDelete={vi.fn()}
        onBulkStatusChange={vi.fn()}
        onBulkAddTag={vi.fn()}
      />
    )
    expect(screen.getByRole('combobox', { name: /status/i })).toBeTruthy()
  })

  it('calls onBulkStatusChange when status selected', () => {
    const onBulkStatusChange = vi.fn()
    render(
      <BulkActionsToolbar
        selectedCount={2}
        onDelete={vi.fn()}
        onBulkStatusChange={onBulkStatusChange}
        onBulkAddTag={vi.fn()}
      />
    )
    fireEvent.change(screen.getByRole('combobox', { name: /status/i }), {
      target: { value: 'deprecated' },
    })
    expect(onBulkStatusChange).toHaveBeenCalledWith('deprecated')
  })

  it('renders tag input when 2+ nodes selected', () => {
    render(
      <BulkActionsToolbar
        selectedCount={3}
        onDelete={vi.fn()}
        onBulkStatusChange={vi.fn()}
        onBulkAddTag={vi.fn()}
      />
    )
    expect(screen.getByPlaceholderText(/add tag/i)).toBeTruthy()
  })

  it('calls onBulkAddTag on Enter in tag input', () => {
    const onBulkAddTag = vi.fn()
    render(
      <BulkActionsToolbar
        selectedCount={2}
        onDelete={vi.fn()}
        onBulkStatusChange={vi.fn()}
        onBulkAddTag={onBulkAddTag}
      />
    )
    const input = screen.getByPlaceholderText(/add tag/i)
    fireEvent.change(input, { target: { value: 'important' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onBulkAddTag).toHaveBeenCalledWith('important')
  })

  it('clears tag input after Enter', () => {
    render(
      <BulkActionsToolbar
        selectedCount={2}
        onDelete={vi.fn()}
        onBulkStatusChange={vi.fn()}
        onBulkAddTag={vi.fn()}
      />
    )
    const input = screen.getByPlaceholderText(/add tag/i) as HTMLInputElement
    fireEvent.change(input, { target: { value: 'important' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(input.value).toBe('')
  })

  it('does not call onBulkAddTag for empty tag', () => {
    const onBulkAddTag = vi.fn()
    render(
      <BulkActionsToolbar
        selectedCount={2}
        onDelete={vi.fn()}
        onBulkStatusChange={vi.fn()}
        onBulkAddTag={onBulkAddTag}
      />
    )
    const input = screen.getByPlaceholderText(/add tag/i)
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onBulkAddTag).not.toHaveBeenCalled()
  })
})
