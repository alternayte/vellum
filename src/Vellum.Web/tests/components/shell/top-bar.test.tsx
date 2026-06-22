import { describe, it, expect, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { TopBar } from '../../../src/components/shell/top-bar'
import { useShellStore } from '../../../src/stores/shell-store'
import { useCanvasStore } from '../../../src/stores/canvas-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
  useShellStore.getState().closeCommandPalette()
})

describe('TopBar', () => {
  it('renders the Vellum logo text', () => {
    render(<TopBar />)
    expect(screen.getByText('Vellum')).toBeTruthy()
  })

  it('renders workspace and project selectors', () => {
    render(<TopBar />)
    expect(screen.getByText(/Workspace/)).toBeTruthy()
    expect(screen.getByText(/Project/)).toBeTruthy()
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
