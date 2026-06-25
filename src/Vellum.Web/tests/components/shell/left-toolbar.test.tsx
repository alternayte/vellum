import { describe, it, expect, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { LeftToolbar } from '../../../src/components/shell/left-toolbar'
import { useShellStore } from '../../../src/stores/shell-store'

beforeEach(() => {
  useShellStore.getState().reset()
})

describe('LeftToolbar', () => {
  it('renders toolbar icons', () => {
    render(<LeftToolbar />)
    expect(screen.getByLabelText('Project files')).toBeDefined()
    expect(screen.getByLabelText('Create new')).toBeDefined()
    expect(screen.getByLabelText('Element catalog')).toBeDefined()
    expect(screen.getByLabelText('Search')).toBeDefined()
  })

  it('toggles files flyout on click', () => {
    render(<LeftToolbar />)
    fireEvent.click(screen.getByLabelText('Project files'))
    expect(useShellStore.getState().activeFlyout).toBe('files')
  })

  it('toggles catalog flyout on click', () => {
    render(<LeftToolbar />)
    fireEvent.click(screen.getByLabelText('Element catalog'))
    expect(useShellStore.getState().activeFlyout).toBe('catalog')
  })

  it('toggles create flyout on click', () => {
    render(<LeftToolbar />)
    fireEvent.click(screen.getByLabelText('Create new'))
    expect(useShellStore.getState().activeFlyout).toBe('create')
  })

  it('renders flyout container when a flyout is active', () => {
    useShellStore.getState().openFlyout('files')
    render(
      <LeftToolbar>
        <div data-testid="flyout-content">Content</div>
      </LeftToolbar>
    )
    expect(screen.getByTestId('flyout-content')).toBeDefined()
  })

  it('does not render flyout container when no flyout is active', () => {
    render(
      <LeftToolbar>
        <div data-testid="flyout-content">Content</div>
      </LeftToolbar>
    )
    expect(screen.queryByTestId('flyout-content')).toBeNull()
  })

  it('closes flyout when backdrop is clicked', () => {
    useShellStore.getState().openFlyout('files')
    render(<LeftToolbar />)
    fireEvent.click(screen.getByTestId('flyout-backdrop'))
    expect(useShellStore.getState().activeFlyout).toBeNull()
  })

  it('highlights the active flyout icon', () => {
    useShellStore.getState().openFlyout('files')
    render(<LeftToolbar />)
    const btn = screen.getByLabelText('Project files')
    expect(btn.className).toContain('bg-accent')
  })
})
