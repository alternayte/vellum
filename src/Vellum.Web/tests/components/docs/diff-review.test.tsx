import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { DiffReview } from '../../../src/components/docs/diff-review'

describe('DiffReview', () => {
  it('renders diff between original and suggested content', () => {
    render(
      <DiffReview
        original="# Title\n\nOriginal content"
        suggested="# Title\n\nRevised content"
        onApply={() => {}}
        onDismiss={() => {}}
      />
    )
    expect(screen.getByText('Review Suggested Changes')).toBeDefined()
    expect(screen.getByText('Apply All')).toBeDefined()
    expect(screen.getByText('Dismiss')).toBeDefined()
  })

  it('calls onApply when Apply All is clicked', () => {
    const onApply = vi.fn()
    render(
      <DiffReview
        original="old"
        suggested="new"
        onApply={onApply}
        onDismiss={() => {}}
      />
    )
    fireEvent.click(screen.getByText('Apply All'))
    expect(onApply).toHaveBeenCalledOnce()
  })

  it('calls onDismiss when Dismiss is clicked', () => {
    const onDismiss = vi.fn()
    render(
      <DiffReview
        original="old"
        suggested="new"
        onApply={() => {}}
        onDismiss={onDismiss}
      />
    )
    fireEvent.click(screen.getByText('Dismiss'))
    expect(onDismiss).toHaveBeenCalledOnce()
  })
})
