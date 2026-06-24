import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { DocToolbar } from '../../../src/components/docs/doc-toolbar'

describe('DocToolbar', () => {
  it('renders formatting buttons when in edit mode', () => {
    render(<DocToolbar editorView={null} mode="edit" onToggleMode={vi.fn()} />)
    expect(screen.getByLabelText('Bold')).toBeTruthy()
    expect(screen.getByLabelText('Italic')).toBeTruthy()
    expect(screen.getByLabelText('Heading 1')).toBeTruthy()
    expect(screen.getByLabelText('Link')).toBeTruthy()
    expect(screen.getByLabelText('Code')).toBeTruthy()
    expect(screen.getByLabelText('Bullet list')).toBeTruthy()
    expect(screen.getByLabelText('Task list')).toBeTruthy()
  })

  it('hides formatting buttons in read mode', () => {
    render(<DocToolbar editorView={null} mode="read" onToggleMode={vi.fn()} />)
    expect(screen.queryByLabelText('Bold')).toBeNull()
  })

  it('renders mode toggle button', () => {
    render(<DocToolbar editorView={null} mode="edit" onToggleMode={vi.fn()} />)
    expect(screen.getByText('Read')).toBeTruthy()
  })

  it('shows Edit label when in read mode', () => {
    render(<DocToolbar editorView={null} mode="read" onToggleMode={vi.fn()} />)
    expect(screen.getByText('Edit')).toBeTruthy()
  })
})
