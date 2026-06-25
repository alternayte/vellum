import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { StatusBar } from '../../../src/components/shell/status-bar'
import { useCanvasStore } from '../../../src/stores/canvas-store'

beforeEach(() => {
  useCanvasStore.getState().reset()
})

describe('StatusBar', () => {
  it('renders Tidy and Fit buttons', () => {
    render(<StatusBar />)
    expect(screen.getByText('Tidy')).toBeDefined()
    expect(screen.getByText('Fit')).toBeDefined()
  })

  it('calls onTidy when Tidy is clicked', () => {
    const onTidy = vi.fn()
    render(<StatusBar onTidy={onTidy} />)
    fireEvent.click(screen.getByText('Tidy'))
    expect(onTidy).toHaveBeenCalledOnce()
  })

  it('calls onZoomToFit when Fit is clicked', () => {
    const onZoomToFit = vi.fn()
    render(<StatusBar onZoomToFit={onZoomToFit} />)
    fireEvent.click(screen.getByText('Fit'))
    expect(onZoomToFit).toHaveBeenCalledOnce()
  })

  it('displays the current zoom level', () => {
    useCanvasStore.getState().setZoom(0.75)
    render(<StatusBar />)
    expect(screen.getByText('75%')).toBeDefined()
  })
})
