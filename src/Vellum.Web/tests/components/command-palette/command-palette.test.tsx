import { describe, it, expect, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { CommandPalette } from '../../../src/components/command-palette/command-palette'
import { useShellStore } from '../../../src/stores/shell-store'

// cmdk uses ResizeObserver and scrollIntoView internally; jsdom doesn't provide them
class ResizeObserverMock {
  observe() {}
  unobserve() {}
  disconnect() {}
}
;(globalThis as Record<string, unknown>).ResizeObserver = ResizeObserverMock

// jsdom doesn't implement scrollIntoView
window.HTMLElement.prototype.scrollIntoView = function () {}

const MOCK_ELEMENTS = [
  { id: '1', kind: 'system', name: 'Orders' },
  { id: '2', kind: 'system', name: 'Payments' },
]

beforeEach(() => {
  useShellStore.getState().closeCommandPalette()
})

describe('CommandPalette', () => {
  it('does not render when closed', () => {
    render(<CommandPalette elements={MOCK_ELEMENTS} />)
    expect(screen.queryByPlaceholderText('Search elements, actions...')).toBeNull()
  })

  it('renders when open with elements and actions', () => {
    useShellStore.getState().toggleCommandPalette()
    render(<CommandPalette elements={MOCK_ELEMENTS} />)
    expect(screen.getByPlaceholderText('Search elements, actions...')).toBeTruthy()
    expect(screen.getByText('Orders')).toBeTruthy()
    expect(screen.getByText('Add element')).toBeTruthy()
    expect(screen.getByText('Toggle status lens')).toBeTruthy()
  })
})
