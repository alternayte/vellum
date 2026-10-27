import { describe, it, expect, beforeEach, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { TopBar } from '../../../src/components/shell/top-bar'
import { useShellStore } from '../../../src/stores/shell-store'
import { useCanvasStore } from '../../../src/stores/canvas-store'

vi.mock('@tanstack/react-router', () => ({
  useNavigate: () => vi.fn(),
}))

// The inbox reads the server; this file tests the bar's own controls.
vi.mock('../../../src/components/comments/inbox-button', () => ({ InboxButton: () => null }))

vi.mock('../../../src/components/shell/account-menu', () => ({ AccountMenu: () => null }))

beforeEach(() => {
  useCanvasStore.getState().reset()
  useShellStore.getState().closeCommandPalette()
})

describe('TopBar', () => {
  it('renders the home/hamburger button', () => {
    render(<TopBar />)
    expect(screen.getByLabelText('Home')).toBeTruthy()
  })

  it('renders project name when provided', () => {
    render(<TopBar projectName="My Project" />)
    expect(screen.getByText('My Project')).toBeTruthy()
  })

  it('renders the search button with keyboard shortcut', () => {
    render(<TopBar />)
    expect(screen.getByText('Search')).toBeTruthy()
    expect(screen.getByText('⌘K')).toBeTruthy()
  })

  it('clicking Search button opens command palette', () => {
    render(<TopBar />)
    fireEvent.click(screen.getByText('Search'))
    expect(useShellStore.getState().commandPaletteOpen).toBe(true)
  })

  it('renders the strata breadcrumb with Context link', () => {
    render(<TopBar />)
    expect(screen.getByText('Context')).toBeTruthy()
  })
})
