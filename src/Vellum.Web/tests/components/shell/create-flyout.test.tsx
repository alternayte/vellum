import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { CreateFlyout } from '../../../src/components/shell/create-flyout'
import { useShellStore } from '../../../src/stores/shell-store'

beforeEach(() => {
  useShellStore.getState().reset()
})

describe('CreateFlyout', () => {
  it('renders New Diagram and New Doc sections', () => {
    render(<CreateFlyout onCreateDiagram={vi.fn()} onCreateDoc={vi.fn()} />)
    expect(screen.getByText('New Diagram')).toBeDefined()
    expect(screen.getByText('New Doc')).toBeDefined()
  })

  it('creates a diagram with the entered name', () => {
    const onCreateDiagram = vi.fn()
    render(<CreateFlyout onCreateDiagram={onCreateDiagram} onCreateDoc={vi.fn()} />)
    fireEvent.click(screen.getByText('New Diagram'))
    const input = screen.getByPlaceholderText('Diagram name')
    fireEvent.change(input, { target: { value: 'My Diagram' } })
    fireEvent.submit(input.closest('form')!)
    expect(onCreateDiagram).toHaveBeenCalledWith('My Diagram')
  })

  it('shows template options for docs', () => {
    render(<CreateFlyout onCreateDiagram={vi.fn()} onCreateDoc={vi.fn()} />)
    fireEvent.click(screen.getByText('New Doc'))
    expect(screen.getByText('Blank')).toBeDefined()
    expect(screen.getByText('SDD')).toBeDefined()
    expect(screen.getByText('ADR')).toBeDefined()
    expect(screen.getByText('PRD')).toBeDefined()
  })

  it('creates a doc from a template', () => {
    const onCreateDoc = vi.fn()
    render(<CreateFlyout onCreateDiagram={vi.fn()} onCreateDoc={onCreateDoc} />)
    fireEvent.click(screen.getByText('New Doc'))
    fireEvent.click(screen.getByText('Blank'))
    const input = screen.getByPlaceholderText('Document title')
    fireEvent.change(input, { target: { value: 'My Doc' } })
    fireEvent.submit(input.closest('form')!)
    expect(onCreateDoc).toHaveBeenCalledWith('My Doc', '', undefined)
  })
})
