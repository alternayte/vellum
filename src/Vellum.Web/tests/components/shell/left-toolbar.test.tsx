import { describe, it, expect, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { LeftToolbar } from '../../../src/components/shell/left-toolbar'
import { useShellStore } from '../../../src/stores/shell-store'

const EMPTY = { hasElements: false, canShare: true }
const MODELLED = { hasElements: true, canShare: true }

beforeEach(() => {
  useShellStore.getState().reset()
})

describe('LeftToolbar', () => {
  it('shows Model, Flows and Sources while the model is empty', () => {
    render(<LeftToolbar access={EMPTY} />)
    const names = screen.getAllByRole('button').map((b) => b.getAttribute('aria-label'))
    expect(names.filter((n) => n !== 'Collapse sidebar')).toEqual(['Model', 'Flows', 'Sources'])
  })

  it('shows Check and Ship once the model holds an element', () => {
    render(<LeftToolbar access={MODELLED} />)
    const names = screen.getAllByRole('button').map((b) => b.getAttribute('aria-label'))
    expect(names.filter((n) => n !== 'Collapse sidebar')).toEqual(['Model', 'Flows', 'Check', 'Ship', 'Sources'])
  })

  it('closes a Check tool when the model becomes empty', () => {
    useShellStore.getState().openFlyout('review')
    const { rerender } = render(<LeftToolbar access={MODELLED} />)
    expect(useShellStore.getState().activeFlyout).toBe('review')
    rerender(<LeftToolbar access={EMPTY} />)
    expect(useShellStore.getState().activeFlyout).toBeNull()
  })

  it('opens an area on its first tool, then on the tool it showed last', () => {
    render(<LeftToolbar access={MODELLED} />)
    fireEvent.click(screen.getByRole('button', { name: 'Check' }))
    expect(useShellStore.getState().activeFlyout).toBe('review')

    fireEvent.click(screen.getByRole('tab', { name: 'Costs' }))
    expect(useShellStore.getState().activeFlyout).toBe('costs')

    fireEvent.click(screen.getByRole('button', { name: 'Check' }))
    expect(useShellStore.getState().activeFlyout).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Check' }))
    expect(useShellStore.getState().activeFlyout).toBe('costs')
  })

  it('shows the tools of the open area as tabs', () => {
    useShellStore.getState().openFlyout('files')
    render(<LeftToolbar access={EMPTY}><div /></LeftToolbar>)
    const tabs = screen.getAllByRole('tab').map((t) => t.textContent)
    expect(tabs).toEqual(['Catalog', 'Contracts', 'Create', 'Files'])
    expect(screen.getByRole('tab', { name: 'Files' }).getAttribute('aria-selected')).toBe('true')
  })

  it('hides Share links from a person who cannot share', () => {
    useShellStore.getState().openFlyout('build')
    render(<LeftToolbar access={{ hasElements: true, canShare: false }}><div /></LeftToolbar>)
    expect(screen.queryByRole('tab')).toBeNull()
  })

  it('does not render the flyout container when no flyout is active', () => {
    render(
      <LeftToolbar access={EMPTY}>
        <div data-testid="flyout-content">Content</div>
      </LeftToolbar>
    )
    expect(screen.queryByTestId('flyout-content')).toBeNull()
  })

  it('closes the flyout when the backdrop is clicked', () => {
    useShellStore.getState().openFlyout('files')
    render(<LeftToolbar access={EMPTY} />)
    fireEvent.click(screen.getByTestId('flyout-backdrop'))
    expect(useShellStore.getState().activeFlyout).toBeNull()
  })

  it('highlights the area of the open tool', () => {
    useShellStore.getState().openFlyout('contracts')
    render(<LeftToolbar access={EMPTY} />)
    expect(screen.getByRole('button', { name: 'Model' }).className).toContain('bg-accent')
  })
})
